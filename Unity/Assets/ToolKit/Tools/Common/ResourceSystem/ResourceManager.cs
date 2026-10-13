/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 资源管理器 (P1, §3.1/§6.2)。统一入口：注册加载器/工厂、LoadAsync、RentAsync、
 *                UnloadUnusedAsync、ClearPoolAsync、Tick、快照与退出释放。统一初始化后开放入口，
 *                运行中可新增来源与工厂；已有来源只允许在初始化完成前替换实现。
 *                LoadManager 负责加载器路由与资源生命周期；本类负责请求边界、工厂入口与退出释放。
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
    /// Load/Rent/卸载的异步 API 可从任意线程调用。
    /// 注册、InitializeAsync 的发起、Tick、GetSnapshot 在 Unity 下须从主线程调用。
    /// Dispose 可从任意线程调用，立即拒绝新请求，投递清理后返回。
    /// </summary>
    public sealed class ResourceManager : IDisposable
    {
        private readonly IExecutionContext _context;
        private readonly IErrorMapper _errorMapper;
        private readonly IResourceDiagnostics _diagnostics;
        private readonly Func<double> _monotonicNow;
        private readonly string _defaultFactory;
        private readonly TimeSpan _maintenanceInterval;
        private readonly TimeSpan? _requestTimeout;

        private readonly Dictionary<string, FactoryRegistration> _factories =
            new Dictionary<string, FactoryRegistration>(StringComparer.Ordinal);

        private readonly LoadManager _loads;
        private readonly InstancePool _pool;
        private readonly FileCache? _fileCache;
        private readonly CancellationTokenSource _disposeCancellation = new CancellationTokenSource();
        private Task? _initialization;
        private double _lastTick;
        private int _disposed;

        /// <param name="monotonicNow">单调时钟 (秒)；测试可注入可控时间源</param>
        /// <param name="fileCache">加载器共享的文件缓存；由本管理器统一初始化，仍由创建方负责释放。不使用文件缓存时省略。</param>
        public ResourceManager(
            IExecutionContext context,
            ResourceSystemOptions? options = null,
            IErrorMapper? errorMapper = null,
            IResourceDiagnostics? diagnostics = null,
            Func<double>? monotonicNow = null,
            FileCache? fileCache = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _fileCache = fileCache;
            options ??= new ResourceSystemOptions();
            options.Validate();
            // 冻结策略快照 (R28)：构造后修改原 options 不影响实际行为。
            var poolSnapshot = PoolPolicy.Clone(options.Pool);
            _defaultFactory = options.DefaultFactory;
            _maintenanceInterval = options.MaintenanceInterval;
            _requestTimeout = options.RequestTimeout;
            _errorMapper = errorMapper ?? DefaultErrorMapper.Instance;
            _diagnostics = new SafeResourceDiagnostics(diagnostics ?? NullResourceDiagnostics.Instance);
            _monotonicNow = monotonicNow ?? _DefaultMonotonicNow;
            _lastTick = _monotonicNow();

            var store = new ResourceStore();
            _loads = new LoadManager(
                _context, store, _diagnostics, () => IsDisposed);
            _pool = new InstancePool(
                _context, _loads, _diagnostics, _monotonicNow, poolSnapshot, () => IsDisposed);
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

        /// <summary> 已发起退出释放；不表示所有异步后端都已完成清理。 </summary>
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary> 统一初始化已成功完成；退出事实由 IsDisposed 单独表示。 </summary>
        public bool IsInitialized => Volatile.Read(ref _initialization)?.Status == TaskStatus.RanToCompletion;

        #region 组装与启动

        /// <summary> 每种来源注册一个加载器；运行中可新增来源，重复注册报错。 </summary>
        public void RegisterLoader(ResourceSource source, IResourceLoader loader, LoaderPolicy? policy = null)
        {
            LoadManager.ValidateSource(source);
            if (loader == null) throw new ArgumentNullException(nameof(loader));
            _Register(() => _loads.RegisterLoader(source, loader, policy));
        }

        /// <summary> 组装时替换来源的实现；仅初始化完成前允许，不支持运行中热替换。 </summary>
        public void ReplaceLoader(ResourceSource source, IResourceLoader loader, LoaderPolicy? policy = null)
        {
            LoadManager.ValidateSource(source);
            if (loader == null) throw new ArgumentNullException(nameof(loader));
            _Register(() =>
            {
                if (IsInitialized)
                    throw new InvalidOperationException("资源管理器已启动，不能替换已注册的加载器");
                _loads.ReplaceLoader(source, loader, policy);
            });
        }

        public void RegisterFactory(string name, IInstanceFactory factory, PoolPolicy? policy = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("名称不能为空", nameof(name));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _Register(() =>
            {
                if (_factories.ContainsKey(name))
                {
                    throw new InvalidOperationException($"实例工厂已注册: {name}");
                }
                _factories[name] = new FactoryRegistration(name, factory, _CloneOrValidate(policy));
            });
        }

        private void _Register(Action register)
        {
            _context.Invoke(() =>
            {
                if (IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(ResourceManager));
                }
                register();
            });
        }

        /// <summary>
        /// 唯一启动入口：执行一次组装配置，初始化共享文件缓存，成功后开放资源请求。
        /// 重复调用等待同一次初始化；调用者取消只停止本次等待，Dispose 阻止初始化后继续使用。
        /// configure 在执行上下文内调用；再次传入配置时报错，运行期新增注册直接使用注册接口。
        /// </summary>
        public Task InitializeAsync(Action<ResourceManager>? configure = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<object>? initializationToStart = null;
            var initialization = _context.Invoke(() =>
            {
                if (IsDisposed) throw new ObjectDisposedException(nameof(ResourceManager));
                if (_initialization == null)
                {
                    // 先登记唯一任务，再执行配置，避免配置回调重入时重复初始化。
                    initializationToStart = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _initialization = initializationToStart.Task;
                }
                else if (configure != null)
                {
                    throw new InvalidOperationException("初始化已开始；新增加载器请直接使用注册接口");
                }
                return _initialization;
            });

            if (initializationToStart != null)
                _ = _InitializeAsync(initializationToStart, configure);
            return initialization.WaitWithCancellation(cancellationToken);
        }

        private async Task _InitializeAsync(TaskCompletionSource<object> completion, Action<ResourceManager>? configure)
        {
            try
            {
                _context.Invoke(() =>
                {
                    if (IsDisposed) throw new ObjectDisposedException(nameof(ResourceManager));
                    configure?.Invoke(this);
                });
                _disposeCancellation.Token.ThrowIfCancellationRequested();
                if (_fileCache != null)
                    await _fileCache.InitializeAsync(_disposeCancellation.Token).ConfigureAwait(false);
                await _context.RunAsync(() =>
                {
                    _disposeCancellation.Token.ThrowIfCancellationRequested();
                    if (IsDisposed) throw new ObjectDisposedException(nameof(ResourceManager));
                    completion.TrySetResult(null!);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                _ = completion.Task.Exception; // 所有调用者取消等待后，仍观察共享初始化的失败。
            }
        }

        #endregion

        #region 加载入口 (§6.2)

        /// <summary> 按明确指定的来源加载资源。 </summary>
        public async Task<ResourceRef<T>> LoadAsync<T>(
            string address,
            ResourceSource source,
            RequestOptions? options = null,
            CancellationToken cancellationToken = default) where T : class
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("address 不能为空", nameof(address));
            }

            LoadManager.ValidateSource(source);
            var requestAccepted = false;
            try
            {
                _EnsureRunning();
                var registration = _loads.Route(source);
                requestAccepted = true;

                var timeout = options?.Timeout ?? _requestTimeout;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);
                if (timeout is TimeSpan ts && ts > TimeSpan.Zero)
                {
                    linked.CancelAfter(ts);
                }

                var request = new ResourceRequest(source, address, typeof(T), options?.Parameters);
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
            catch (InvalidOperationException) when (!requestAccepted)
            {
                // 未接收请求前的启动顺序/执行上下文误用，直接报告给调用方。
                throw;
            }
            catch (Exception ex)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InternalUnexpected, LoadStage.WaitForLoad,
                    CleanupStatus.Unknown, ex,
                    new Dictionary<string, object> { { "source", source }, { "address", address }, }));
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
            var error = IsDisposed
                ? new LoadError(DiagnosticCodes.LifecycleManagerDisposed, LoadStage.Dispose, CleanupStatus.Complete)
                : new LoadError(DiagnosticCodes.RequestTimeout, LoadStage.WaitForLoad, CleanupStatus.Complete);
            return new ResourceLoadException(_MapUserCode(error));
        }

        private LoadError _MapUserCode(LoadError error)
        {
            return error.WithUserCode(_errorMapper.Map(error.DiagnosticCode, error.Stage, error.Context));
        }

        #endregion

        #region 实例租用 (§7.3)

        /// <summary> 按明确指定的来源租用实例。 </summary>
        public async Task<InstanceLease<T>> RentAsync<T>(
            string address,
            ResourceSource source,
            string? factory = null,
            RequestOptions? options = null,
            CancellationToken cancellationToken = default) where T : class
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("address 不能为空", nameof(address));
            }

            LoadManager.ValidateSource(source);
            var requestAccepted = false;
            try
            {
                var (factoryReg, loaderReg) = await _context.RunAsync(() =>
                {
                    _EnsureRunning();
                    var f = _GetFactoryOrThrow(factory ?? _defaultFactory);
                    var l = _loads.Route(source);
                    return (f, l);
                }).ConfigureAwait(false);
                requestAccepted = true;

                var timeout = options?.Timeout ?? _requestTimeout;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _disposeCancellation.Token);
                if (timeout is TimeSpan ts && ts > TimeSpan.Zero)
                {
                    linked.CancelAfter(ts);
                }

                var instanceRequest = new ResourceRequest(loaderReg.Source, address, typeof(T), options?.Parameters);
                var prototypeRequest = factoryReg.Factory.GetPrototypeRequest(instanceRequest);
                var protoRegistration = _loads.Route(prototypeRequest.Source);

                var resolved = await _loads.ResolveAsync(protoRegistration, prototypeRequest, linked.Token)
                    .ConfigureAwait(false);

                var instanceKey = factoryReg.Factory.GetInstanceKey(instanceRequest);
                var poolKey = new PoolKey(
                    new ResourceKey(protoRegistration.Source, resolved.LocalKey),
                    factoryReg.Name,
                    instanceKey ?? "");
                Func<PoolBucket, InstanceRecord, object> leaseFactory =
                    (bucket, record) => new InstanceLease<T>(_pool, bucket, record);

                var (completion, bucketToInitialize) = await _context.RunAsync(
                    () => _pool.JoinOrRent(
                        poolKey, factoryReg, instanceRequest,
                        typeof(T), leaseFactory, linked.Token)).ConfigureAwait(false);
                if (bucketToInitialize != null)
                {
                    // 桶登记完成后再启动原型加载，避免同步完成路径仍嵌套在登记委托内。
                    _ = _pool.InitializeAsync(bucketToInitialize, protoRegistration, resolved);
                }
                var boxed = await completion.ConfigureAwait(false);
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
            catch (InvalidOperationException) when (!requestAccepted)
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
                        { "source", source }, { "factoryId", factory ?? _defaultFactory }, { "address", address },
                    }));
            }
        }

        #endregion

        #region 维护与快照

        /// <summary> Unity 驱动器定时调用；后台任务必须回到上下文提交状态 </summary>
        public void Tick()
        {
            _context.Invoke(() =>
            {
                if (!IsInitialized || IsDisposed)
                {
                    return;
                }
                var now = _monotonicNow();
                if (now - _lastTick < _maintenanceInterval.TotalSeconds)
                {
                    return;
                }
                _lastTick = now;
                _pool.TickMaintenance(now);
            });
        }

        /// <summary>
        /// 业务选择本次卸载范围；null 表示全部。筛选在状态提交之外执行，卸载前重新检查持有状态。
        /// 只释放无人持有的产物；失败进入脏集合，不影响其他项。取消只停止等待，已开始的释放继续。
        /// </summary>
        public Task<ResourceUnloadResult> UnloadUnusedAsync(
            Predicate<ResourceRow>? select = null, CancellationToken cancellationToken = default)
        {
            _EnsureRunning();
            return _loads.UnloadUnusedAsync(select, cancellationToken);
        }

        /// <summary> 关闭指定来源与业务地址的全部池代际。 </summary>
        public async Task ClearPoolAsync(
            string address,
            ResourceSource source,
            string? factory = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("address 不能为空", nameof(address));
            }
            LoadManager.ValidateSource(source);
            var tasks = await _context.RunAsync(() =>
                _pool.CloseBuckets(address, source, factory ?? _defaultFactory)).ConfigureAwait(false);
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
                IsInitialized = IsInitialized,
                IsDisposed = IsDisposed,
                ResourceRows = _loads.SnapshotRows(),
                PoolRows = _pool.SnapshotRows(),
                DirtyResources = _loads.SnapshotDirtyResources(),
            });
        }

        #endregion

        #region 退出释放

        /// <summary>
        /// 随应用退出调用；立即作废业务入口与凭证，然后销毁实例、释放底层资源。
        /// 不等待业务归还，不阻塞线程；异步后端由各自操作完成收尾，失败只报告诊断。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _ = _DisposeOwnedResourcesAsync();
        }

        private async Task _DisposeOwnedResourcesAsync()
        {
            try { _disposeCancellation.Cancel(); }
            catch (Exception ex)
            {
                _diagnostics.Report(new LoadError(DiagnosticCodes.ObserverCallbackFailed,
                    LoadStage.Dispose, CleanupStatus.Unknown, ex));
            }
            try
            {
                await _loads.CancelPendingLoadsAsync().ConfigureAwait(false);
                // 只等待实际的实例创建/销毁收尾，不等待租约归还；原型在此期间必须仍然存在。
                var poolDisposal = await _context.RunAsync(_pool.DisposeAsync).ConfigureAwait(false);
                await poolDisposal.ConfigureAwait(false);
                await _loads.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _diagnostics.Report(new LoadError(DiagnosticCodes.InternalUnexpected,
                    LoadStage.Dispose, CleanupStatus.Unknown, ex));
            }
        }

        #endregion

        #region 私有

        private void _EnsureRunning()
        {
            if (IsDisposed)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LifecycleManagerDisposed, LoadStage.Route, CleanupStatus.Complete));
            }
            if (!IsInitialized)
            {
                throw new InvalidOperationException("资源管理器尚未就绪：请先等待 InitializeAsync 成功完成");
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
                        { "factoryId", name ?? "" }, { "registered", string.Join(",", _factories.Keys.ToArray()) },
                    }));
            }
            return registration;
        }

        #endregion
    }
}
