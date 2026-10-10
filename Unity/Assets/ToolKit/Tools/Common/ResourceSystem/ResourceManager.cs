/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 资源管理器 (P1, §3.1/§6.2)。统一入口：注册加载器/工厂、LoadAsync、RentAsync、
 *                UnloadUnusedAsync、ClearPoolAsync、Tick、快照与关闭。注册只在初始化阶段完成，
 *                首次请求使注册表冻结；运行时替换加载器通过建立新管理器完成。
 *                LoadManager 负责加载器路由与资源生命周期；本类负责请求边界、工厂入口与整体关闭。
 *                主 API 失败抛 ResourceLoadException；主动取消抛 OperationCanceledException。
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 具名实例工厂注册项：策略已冻结 </summary>
    internal sealed class FactoryRegistration
    {
        public readonly string Name;
        public readonly IInstanceFactory Factory;
        public readonly PoolPolicy? Policy;

        public FactoryRegistration(string name, IInstanceFactory factory, PoolPolicy? policy)
        {
            Name = name;
            Factory = factory;
            Policy = policy;
        }
    }

    /// <summary>
    /// Load/Rent/卸载/关闭的异步 API 可从任意线程调用。
    /// 注册、Tick、GetSnapshot 是同步 API；Unity 下须在主线程调用。
    /// Dispose 可从任意线程调用，立即拒绝新请求，投递清理后返回。
    /// </summary>
    public sealed class ResourceManager : IDisposable
    {
        private readonly IExecutionContext _context;
        private readonly ResourceSystemOptions _options;
        private readonly IErrorMapper _errorMapper;
        private readonly IResourceDiagnostics _diagnostics;
        private readonly Func<double> _monotonicNow;
        // 冻结的策略快照 (R28)：构造后修改原 options 不影响实际行为
        private readonly MemoryPolicy _memorySnapshot;
        private readonly PoolPolicy _poolSnapshot;
        private readonly string _defaultLoader;
        private readonly string _defaultFactory;
        private readonly TimeSpan _maintenanceInterval;
        private readonly TimeSpan? _requestTimeout;

        private readonly Dictionary<string, FactoryRegistration> _factories =
            new Dictionary<string, FactoryRegistration>(StringComparer.Ordinal);

        private readonly ResourceStore _store;
        private readonly LoadManager _loads;
        private readonly InstancePool _pool;
        private readonly CancellationTokenSource _stopNewRequests = new CancellationTokenSource();
        private readonly TaskCompletionSource<object> _shutdownTcs =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        private ManagerState _state;
        private int _activeRequests;
        private double _lastTick;
        private int _shutdownStarted;

        /// <param name="monotonicNow">单调时钟 (秒)；测试可注入可控时间源</param>
        public ResourceManager(
            IExecutionContext context,
            ResourceSystemOptions? options = null,
            IErrorMapper? errorMapper = null,
            IResourceDiagnostics? diagnostics = null,
            Func<double>? monotonicNow = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _options = options ?? new ResourceSystemOptions();
            _options.Validate();
            // 冻结为内部不可变快照 (R28)：系统与具名策略在装配后不再受外部修改影响
            _memorySnapshot = MemoryPolicy.Clone(_options.Memory);
            _poolSnapshot = PoolPolicy.Clone(_options.Pool);
            _defaultLoader = _options.DefaultLoader;
            _defaultFactory = _options.DefaultFactory;
            _maintenanceInterval = _options.MaintenanceInterval;
            _requestTimeout = _options.RequestTimeout;
            _errorMapper = errorMapper ?? DefaultErrorMapper.Instance;
            _diagnostics = new SafeResourceDiagnostics(diagnostics ?? NullResourceDiagnostics.Instance);
            _monotonicNow = monotonicNow ?? _DefaultMonotonicNow;
            _lastTick = _monotonicNow();

            _store = new ResourceStore();
            _loads = new LoadManager(
                _context, _store, _defaultLoader, _diagnostics, _monotonicNow, _memorySnapshot,
                () => _state == ManagerState.Running && Volatile.Read(ref _shutdownStarted) == 0,
                _CheckShutdownComplete);
            _pool = new InstancePool(
                _context, _loads, _diagnostics, _monotonicNow, _poolSnapshot,
                () => _state == ManagerState.Running && Volatile.Read(ref _shutdownStarted) == 0,
                _CheckShutdownComplete);
        }

        /// <summary> 工厂策略快照：未提供时保持 null (桶创建时继承系统默认快照)，显式提供才克隆并验证 </summary>
        private static PoolPolicy? _CloneOrValidate(PoolPolicy? policy)
        {
            if (policy == null)
            {
                return null;
            }
            var snapshot = PoolPolicy.Clone(policy);
            snapshot.Validate();
            return snapshot;
        }

        private static double _DefaultMonotonicNow()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        internal ManagerState State => _state;

        #region 组合装配 (仅配置期)

        /// <summary> 注册加载器；仅配置期允许；重复名称报错 (R31)，策略验证并冻结 (R28) </summary>
        public void RegisterLoader(string name, IResourceLoader loader, LoaderPolicy? policy = null)
        {
            _Register(name, () => _loads.RegisterLoader(name, loader, policy), name, loader);
        }

        /// <summary> 显式替换加载器；仅配置期允许；并发配置由 LoadManager 管理。 </summary>
        public void ReplaceLoader(string name, IResourceLoader loader, LoaderPolicy? policy = null)
        {
            _Register(name, () => _loads.ReplaceLoader(name, loader, policy), name, loader);
        }

        public void RegisterFactory(string name, IInstanceFactory factory, PoolPolicy? policy = null)
        {
            _Register(name, () =>
            {
                if (_factories.ContainsKey(name))
                {
                    throw new InvalidOperationException($"实例工厂已注册: {name}");
                }
                _factories[name] = new FactoryRegistration(name, factory, _CloneOrValidate(policy));
            }, name, factory);
        }

        private void _Register(string name, Action mutate, string argName, object argValue)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("名称不能为空", nameof(name));
            }
            if (argValue == null)
            {
                throw new ArgumentNullException(argName);
            }
            _context.Invoke(() =>
            {
                if (_state != ManagerState.Configuring || Volatile.Read(ref _shutdownStarted) != 0)
                {
                    // 注册只在初始化阶段完成；运行时替换通过建立新管理器完成
                    throw new InvalidOperationException(
                        $"注册表已冻结 (state={_state})，不能注册或替换 {name}");
                }
                mutate();
            });
        }

        #endregion

        #region 加载入口 (§6.2)

        public async Task<ResourceRef<T>> LoadAsync<T>(
            string address,
            string? loader = null,
            RequestOptions? options = null,
            CancellationToken cancellationToken = default) where T : class
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("address 不能为空", nameof(address));
            }

            var registered = false;
            try
            {
                var registration = await _context.RunAsync(() =>
                {
                    _FreezeRegistry();
                    var reg = _loads.Route(loader);
                    _activeRequests++;
                    return reg;
                }).ConfigureAwait(false);
                registered = true;

                var timeout = options?.Timeout ?? _requestTimeout;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _stopNewRequests.Token);
                if (timeout is TimeSpan ts && ts > TimeSpan.Zero)
                {
                    linked.CancelAfter(ts);
                }

                var request = new ResourceRequest(registration.Name, address, typeof(T), options?.Parameters);
                return await _loads.LoadReferenceAsync<T>(
                    registration, request, options?.Progress, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw _ClassifyCancellation(cancellationToken);
            }
            catch (ResourceLoadException ex)
            {
                // 注入的用户错误映射器在统一交付边界生效 (R30)：诊断证据与 DiagnosticId 不变
                throw new ResourceLoadException(ex.Error.WithUserCode(
                    _errorMapper.Map(ex.Error.DiagnosticCode, ex.Error.Stage, ex.Error.Context)));
            }
            catch (Exception ex)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InternalUnexpected, LoadStage.WaitForLoad,
                    CleanupStatus.Unknown, ex,
                    new Dictionary<string, object>
                    {
                        { "loaderId", loader ?? _defaultLoader },
                        { "address", address },
                    }));
            }
            finally
            {
                if (registered)
                {
                    var context = _context;
                    context.Post(() =>
                    {
                        _activeRequests--;
                        _CheckShutdownComplete();
                    });
                }
            }
        }

        /// <summary>
        /// 取消分类：用户令牌取消 → OCE(userCt)；管理器停止新请求 → SystemClosed；
        /// 其余 (请求超时) → request.timeout。不把超时伪装成用户取消。
        /// </summary>
        private Exception _ClassifyCancellation(CancellationToken userCt)
        {
            if (userCt.IsCancellationRequested)
            {
                return new OperationCanceledException(userCt);
            }
            var error = _stopNewRequests.IsCancellationRequested
                ? new LoadError(DiagnosticCodes.LifecycleManagerClosing, LoadStage.Shutdown, CleanupStatus.Complete)
                : new LoadError(DiagnosticCodes.RequestTimeout, LoadStage.WaitForLoad, CleanupStatus.Complete);
            return new ResourceLoadException(_MapUserCode(error));
        }

        private LoadError _MapUserCode(LoadError error)
        {
            return error.WithUserCode(_errorMapper.Map(error.DiagnosticCode, error.Stage, error.Context));
        }

        #endregion

        #region 实例租用 (§7.3)

        public async Task<InstanceLease<T>> RentAsync<T>(
            string address,
            string? loader = null,
            string? factory = null,
            RequestOptions? options = null,
            CancellationToken cancellationToken = default) where T : class
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("address 不能为空", nameof(address));
            }

            var registered = false;
            try
            {
                var (factoryReg, loaderReg) = await _context.RunAsync(() =>
                {
                    _FreezeRegistry();
                    var f = _GetFactoryOrThrow(factory ?? _defaultFactory);
                    var l = _loads.Route(loader);
                    _activeRequests++;
                    return (f, l);
                }).ConfigureAwait(false);
                registered = true;

                var timeout = options?.Timeout ?? _requestTimeout;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _stopNewRequests.Token);
                if (timeout is TimeSpan ts && ts > TimeSpan.Zero)
                {
                    linked.CancelAfter(ts);
                }

                var instanceRequest = new ResourceRequest(loaderReg.Name, address, typeof(T), options?.Parameters);
                var prototypeRequest = factoryReg.Factory.GetPrototypeRequest(instanceRequest);
                if (string.IsNullOrEmpty(prototypeRequest.LoaderId))
                {
                    prototypeRequest = new ResourceRequest(
                        loaderReg.Name, prototypeRequest.Address, prototypeRequest.RequestedType,
                        prototypeRequest.Parameters);
                }
                var protoRegistration = await _context.RunAsync(() => _loads.Route(prototypeRequest.LoaderId))
                    .ConfigureAwait(false);

                var resolved = await _loads.ResolveAsync(protoRegistration, prototypeRequest, linked.Token)
                    .ConfigureAwait(false);

                var instanceKey = factoryReg.Factory.GetInstanceKey(instanceRequest);
                var poolKey = new PoolKey(
                    new ResourceKey(protoRegistration.Name, resolved.LocalKey),
                    factoryReg.Name,
                    instanceKey ?? "");
                Func<PoolBucket, InstanceRecord, object> leaseFactory =
                    (bucket, record) => new InstanceLease<T>(_pool, bucket, record);

                var task = await _context.RunAsync(
                    () => _pool.JoinOrRent(
                        poolKey, factoryReg, protoRegistration, resolved, instanceRequest,
                        typeof(T), leaseFactory, linked.Token)).ConfigureAwait(false);
                var boxed = await task.ConfigureAwait(false);
                return (InstanceLease<T>)boxed;
            }
            catch (OperationCanceledException)
            {
                throw _ClassifyCancellation(cancellationToken);
            }
            catch (ResourceLoadException ex)
            {
                // 注入的用户错误映射器在统一交付边界生效 (R30)：诊断证据与 DiagnosticId 不变
                throw new ResourceLoadException(ex.Error.WithUserCode(
                    _errorMapper.Map(ex.Error.DiagnosticCode, ex.Error.Stage, ex.Error.Context)));
            }
            catch (Exception ex)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InternalUnexpected, LoadStage.Instantiate,
                    CleanupStatus.Unknown, ex,
                    new Dictionary<string, object>
                    {
                        { "loaderId", loader ?? _defaultLoader },
                        { "factoryId", factory ?? _defaultFactory },
                        { "address", address },
                    }));
            }
            finally
            {
                if (registered)
                {
                    var context = _context;
                    context.Post(() =>
                    {
                        _activeRequests--;
                        _CheckShutdownComplete();
                    });
                }
            }
        }

        #endregion

        #region 维护与快照

        /// <summary> Unity 驱动器定时调用；后台任务必须回到上下文提交状态 </summary>
        public void Tick()
        {
            _context.Invoke(() =>
            {
                if (_state != ManagerState.Running)
                {
                    return;
                }
                var now = _monotonicNow();
                if (now - _lastTick < _maintenanceInterval.TotalSeconds)
                {
                    return;
                }
                _lastTick = now;
                _loads.TickMaintenance();
                _pool.TickMaintenance(now);
            });
        }

        /// <summary> 立即启动所有可释放空闲条目的卸载，等待本批次完成；不影响活跃持有 </summary>
        public async Task UnloadUnusedAsync(CancellationToken cancellationToken = default)
        {
            var tasks = await _context.RunAsync(() => _loads.UnloadAllIdle()).ConfigureAwait(false);
            if (tasks.Count == 0)
            {
                return;
            }
            // ct 只取消调用者等待 (R32)；已开始的底层卸载继续使用自身生命周期
            await Task.WhenAll(tasks).WaitWithCancellation(cancellationToken).ConfigureAwait(false);
        }

        /// <summary> 关闭在调用时捕获的全部池代际，覆盖该业务地址目前关联的已解析版本 </summary>
        public async Task ClearPoolAsync(
            string address,
            string? loader = null,
            string? factory = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("address 不能为空", nameof(address));
            }
            var tasks = await _context.RunAsync(() =>
                _pool.CloseBuckets(address, loader ?? _defaultLoader, factory ?? _defaultFactory))
                .ConfigureAwait(false);
            if (tasks.Count == 0)
            {
                return;
            }
            await Task.WhenAll(tasks).WaitWithCancellation(cancellationToken).ConfigureAwait(false);
        }

        public ResourceSnapshot GetSnapshot()
        {
            return _context.Invoke(() => new ResourceSnapshot
            {
                State = _state,
                ActiveRequests = _activeRequests,
                ResourceRows = _store.SnapshotRows(),
                PoolRows = _pool.SnapshotRows(),
                CleanupErrors = _store.CollectStuckErrors(),
            });
        }

        #endregion

        #region 关闭 (§7.4)

        /// <summary> 停止新请求并启动排空；不等待活跃业务引用归还 </summary>
        public void Dispose()
        {
            _BeginShutdown();
        }

        /// <summary> 启动同一关闭过程并等待；ct 只取消等待，不撤销系统关闭 </summary>
        public async Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            _BeginShutdown();
            await _shutdownTcs.Task.WaitWithCancellation(cancellationToken).ConfigureAwait(false);
        }

        private void _BeginShutdown()
        {
            if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            {
                return;
            }
            _context.Post(() =>
            {
                if (_state == ManagerState.Closing || _state == ManagerState.Closed
                    || _state == ManagerState.Faulted)
                {
                    return;
                }
                _state = ManagerState.Closing;
                _stopNewRequests.Cancel();
                _pool.BeginClose();
                _loads.BeginClose();
                _CheckShutdownComplete();
            });
        }

        internal void _CheckShutdownComplete()
        {
            if (_state != ManagerState.Closing)
            {
                return;
            }
            if (_activeRequests > 0)
            {
                return;
            }
            if (!_store.IsQuiesced || !_pool.IsQuiesced)
            {
                return;
            }

            var stuckErrors = _store.CollectStuckErrors();
            stuckErrors.AddRange(_pool.CollectStuckErrors());

            if (stuckErrors.Count > 0)
            {
                // 确认存在无法完成的清理故障：Faulted，聚合清理诊断
                _state = ManagerState.Faulted;
                var exceptions = new List<Exception>();
                foreach (var error in stuckErrors)
                {
                    exceptions.Add(new ResourceLoadException(error));
                }
                var outstanding = _store.DescribeOutstanding();
                outstanding.AddRange(_pool.DescribeOutstanding());
                if (outstanding.Count > 0)
                {
                    exceptions.Add(new ResourceLoadException(new LoadError(
                        DiagnosticCodes.LifecycleOutstandingOwners, LoadStage.Shutdown,
                        CleanupStatus.Incomplete, null,
                        ToContext(outstanding))));
                }
                _shutdownTcs.TrySetException(new AggregateException(
                    "资源系统关闭存在残留 (清理故障或未归还持有)", exceptions));
                return;
            }

            if (_store.EntryCount == 0 && _pool.AllClosed)
            {
                _state = ManagerState.Closed;
                _shutdownTcs.TrySetResult(null!);
            }
            // 否则：仍有活跃持有者，等待其归还 (Release/Return 在 Closing 下继续清理)
        }

        private static Dictionary<string, object> ToContext(List<string> descriptions)
        {
            var context = new Dictionary<string, object>();
            for (var i = 0; i < descriptions.Count; i++)
            {
                context["owner_" + i] = descriptions[i];
            }
            return context;
        }

        #endregion

        #region 私有

        private void _FreezeRegistry()
        {
            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LifecycleManagerClosing, LoadStage.Route, CleanupStatus.Complete));
            }
            if (_state == ManagerState.Configuring)
            {
                _state = ManagerState.Running; // 首次请求使注册表冻结
                return;
            }
            if (_state != ManagerState.Running)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LifecycleManagerClosing, LoadStage.Route, CleanupStatus.Complete));
            }
        }

        private FactoryRegistration _GetFactoryOrThrow(string name)
        {
            if (string.IsNullOrEmpty(name) || !_factories.TryGetValue(name, out var registration))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderNotRegistered, LoadStage.Route, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "factoryId", name ?? "" },
                        { "registered", string.Join(",", _factories.Keys.ToArray()) },
                    }));
            }
            return registration;
        }

        #endregion
    }
}
