/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 资源管理器 (P1, §3.1/§6.2)。统一入口：注册加载器/工厂、LoadAsync、RentAsync、
 *                UnloadUnusedAsync、ClearPoolAsync、Tick、快照与关闭。注册只在初始化阶段完成，
 *                首次请求使注册表冻结；运行时替换加载器通过建立新管理器完成。
 *                主 API 失败抛 ResourceLoadException；主动取消抛 OperationCanceledException。
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common.Resource
{
    /// <summary> 具名加载器注册项：加载器 + 策略快照 + 加载并发槽位 </summary>
    internal sealed class LoaderRegistration
    {
        public readonly string Name;
        public readonly IResourceLoader Loader;
        public readonly LoaderPolicy? Policy;
        public readonly SemaphoreSlim? LoadSlots;

        public LoaderRegistration(string name, IResourceLoader loader, LoaderPolicy? policy)
        {
            Name = name;
            Loader = loader;
            Policy = policy;
            var max = policy?.MaxConcurrentLoads ?? 0;
            LoadSlots = max > 0 ? new SemaphoreSlim(max, max) : null;
        }
    }

    /// <summary> 具名实例工厂注册项 </summary>
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

    public sealed class ResourceManager : IDisposable
    {
        private readonly IExecutionContext _context;
        private readonly ResourceSystemOptions _options;
        private readonly IErrorMapper _errorMapper;
        private readonly IResourceDiagnostics _diagnostics;
        private readonly Func<double> _monotonicNow;

        private readonly Dictionary<string, LoaderRegistration> _loaders =
            new Dictionary<string, LoaderRegistration>(StringComparer.Ordinal);

        private readonly Dictionary<string, FactoryRegistration> _factories =
            new Dictionary<string, FactoryRegistration>(StringComparer.Ordinal);

        private readonly ResourceStore _store;
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
            _errorMapper = errorMapper ?? DefaultErrorMapper.Instance;
            _diagnostics = diagnostics ?? NullResourceDiagnostics.Instance;
            _monotonicNow = monotonicNow ?? _DefaultMonotonicNow;
            _lastTick = _monotonicNow();

            _store = new ResourceStore(
                _context, _diagnostics, _monotonicNow, _options.Memory,
                () => _state == ManagerState.Running,
                () => _state == ManagerState.Running,
                _CheckShutdownComplete);
            _pool = new InstancePool(
                _context, _store, _diagnostics, _monotonicNow, _options.Pool,
                () => _state == ManagerState.Running,
                _CheckShutdownComplete);
        }

        private static double _DefaultMonotonicNow()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        internal ManagerState State => _state;

        #region 组合装配 (仅配置期)

        /// <summary> 注册加载器；仅配置期允许，重复名称报错 </summary>
        public void RegisterLoader(string name, IResourceLoader loader, LoaderPolicy? policy = null)
        {
            _Register(name, () =>
            {
                _loaders[name] = new LoaderRegistration(name, loader, policy);
            }, name, loader);
        }

        /// <summary> 显式替换加载器；仅配置期允许 </summary>
        public void ReplaceLoader(string name, IResourceLoader loader, LoaderPolicy? policy = null)
        {
            _Register(name, () =>
            {
                if (!_loaders.ContainsKey(name))
                {
                    throw new InvalidOperationException($"替换的加载器不存在: {name}，请先 RegisterLoader");
                }
                _loaders[name] = new LoaderRegistration(name, loader, policy);
            }, name, loader);
        }

        public void RegisterFactory(string name, IInstanceFactory factory, PoolPolicy? policy = null)
        {
            _Register(name, () =>
            {
                _factories[name] = new FactoryRegistration(name, factory, policy);
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
                if (_state != ManagerState.Configuring)
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
                var registration = _context.Invoke(() =>
                {
                    _FreezeRegistry();
                    var reg = _GetLoaderOrThrow(loader ?? _options.DefaultLoader);
                    _activeRequests++;
                    return reg;
                });
                registered = true;

                var timeout = options?.Timeout ?? _options.RequestTimeout;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _stopNewRequests.Token);
                if (timeout is TimeSpan ts && ts > TimeSpan.Zero)
                {
                    linked.CancelAfter(ts);
                }

                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var request = new ResourceRequest(registration.Name, address, typeof(T), options?.Parameters);
                    var resolved = await registration.Loader.ResolveAsync(request, linked.Token).ConfigureAwait(false);
                    _ValidateResolved(resolved, address);

                    var key = new ResourceKey(registration.Name, resolved.LocalKey);
                    var spec = new WaiterSpec(
                        typeof(T), options?.Progress,
                        entry => new ResourceRef<T>(_store, entry, _store.NewLeaseId()));
                    var outcome = _context.Invoke(
                        () => _store.JoinOrAcquire(key, registration, resolved, spec, linked.Token));

                    if (outcome.Barrier != null)
                    {
                        // 不取消旧操作；等待结束后重新 Resolve (版本映射可能已变化)
                        await outcome.Barrier.WaitWithCancellation(linked.Token).ConfigureAwait(false);
                        continue;
                    }
                    var boxed = await outcome.Deliver!.ConfigureAwait(false);
                    return (ResourceRef<T>)boxed;
                }
            }
            catch (OperationCanceledException)
            {
                throw _ClassifyCancellation(cancellationToken);
            }
            catch (ResourceLoadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InternalUnexpected, LoadStage.WaitForLoad,
                    CleanupStatus.Unknown, ex,
                    new Dictionary<string, object>
                    {
                        { "loaderId", loader ?? _options.DefaultLoader },
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
            if (_stopNewRequests.IsCancellationRequested)
            {
                return new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LifecycleManagerClosing, LoadStage.Shutdown, CleanupStatus.Complete));
            }
            return new ResourceLoadException(new LoadError(
                DiagnosticCodes.RequestTimeout, LoadStage.WaitForLoad, CleanupStatus.Complete));
        }

        private static void _ValidateResolved(ResolvedResource resolved, string address)
        {
            if (resolved == null || string.IsNullOrEmpty(resolved.LocalKey))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "address", address } }));
            }
            if (resolved.RepresentationType == null)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "address", address },
                        { "reason", "RepresentationType 不能为空" },
                    }));
            }
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
                var (factoryReg, loaderReg) = _context.Invoke(() =>
                {
                    _FreezeRegistry();
                    var f = _GetFactoryOrThrow(factory ?? _options.DefaultFactory);
                    var l = _GetLoaderOrThrow(loader ?? _options.DefaultLoader);
                    _activeRequests++;
                    return (f, l);
                });
                registered = true;

                var timeout = options?.Timeout ?? _options.RequestTimeout;
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
                var protoRegistration = _context.Invoke(() => _GetLoaderOrThrow(prototypeRequest.LoaderId));

                linked.Token.ThrowIfCancellationRequested();
                var resolved = await protoRegistration.Loader.ResolveAsync(prototypeRequest, linked.Token)
                    .ConfigureAwait(false);
                _ValidateResolved(resolved, address);

                var instanceKey = factoryReg.Factory.GetInstanceKey(instanceRequest);
                var poolKey = new PoolKey(
                    new ResourceKey(protoRegistration.Name, resolved.LocalKey),
                    factoryReg.Name,
                    instanceKey ?? "");
                Func<PoolBucket, InstanceRecord, object> leaseFactory =
                    (bucket, record) => new InstanceLease<T>(_pool, bucket, record);

                var task = _context.Invoke(
                    () => _pool.JoinOrRent(
                        poolKey, factoryReg, protoRegistration, resolved, instanceRequest,
                        typeof(T), leaseFactory, linked.Token));
                var boxed = await task.ConfigureAwait(false);
                return (InstanceLease<T>)boxed;
            }
            catch (OperationCanceledException)
            {
                throw _ClassifyCancellation(cancellationToken);
            }
            catch (ResourceLoadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InternalUnexpected, LoadStage.Instantiate,
                    CleanupStatus.Unknown, ex,
                    new Dictionary<string, object>
                    {
                        { "loaderId", loader ?? _options.DefaultLoader },
                        { "factoryId", factory ?? _options.DefaultFactory },
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
                if (now - _lastTick < _options.MaintenanceInterval.TotalSeconds)
                {
                    return;
                }
                _lastTick = now;
                _store.TickMaintenance();
                _pool.TickMaintenance(now);
            });
        }

        /// <summary> 立即启动所有可释放空闲条目的卸载，等待本批次完成；不影响活跃持有 </summary>
        public async Task UnloadUnusedAsync(CancellationToken cancellationToken = default)
        {
            var tasks = _context.Invoke(() => _store.UnloadAllIdle());
            if (tasks.Count == 0)
            {
                return;
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
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
            var tasks = _context.Invoke(() =>
                _pool.CloseBuckets(address, loader ?? _options.DefaultLoader, factory ?? _options.DefaultFactory));
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
            _context.Invoke(() =>
            {
                if (_state == ManagerState.Closing || _state == ManagerState.Closed
                    || _state == ManagerState.Faulted)
                {
                    return;
                }
                _state = ManagerState.Closing;
                _stopNewRequests.Cancel();
                _pool.BeginClose();
                _store.BeginClose();
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

        private LoaderRegistration _GetLoaderOrThrow(string name)
        {
            if (string.IsNullOrEmpty(name) || !_loaders.TryGetValue(name, out var registration))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderNotRegistered, LoadStage.Route, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "loaderId", name ?? "" },
                        { "registered", string.Join(",", _loaders.Keys.ToArray()) },
                    }));
            }
            return registration;
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
