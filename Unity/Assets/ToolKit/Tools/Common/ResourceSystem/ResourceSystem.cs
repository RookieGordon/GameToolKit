/*
 * description  : 资源系统对外外观。装配时取独立配置快照；初始化成功后才开放加载；
 *                来源加载器可在初始化前后随时 RegisterLoader 注册或替换；
 *                并列组织 ResourceStore 与 InstancePool，池只通过"取原型引用"一条窄路使用资源流程。
 *                退出顺序：关池 → 停止在途加载并等待收尾 → 卸载无人持有的产物 → 逆序执行移交的关闭回调。
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 只读排查快照：不赋予释放权限，不转移所有权 </summary>
    public sealed class ResourceSnapshot
    {
        public List<ResourceEntry> Entries { get; } = new List<ResourceEntry>();
        public List<ResourceResidue> Residues { get; } = new List<ResourceResidue>();
        public List<PoolBucketInfo> Pools { get; } = new List<PoolBucketInfo>();
        public List<InstanceResidue> InstanceResidues { get; } = new List<InstanceResidue>();
    }

    public sealed class ResourceSystem : IAsyncDisposable
    {
        private readonly IExecutionContext _context;
        private readonly ResourceStore _store;
        private readonly Dictionary<Type, IProductConverter> _converters;
        /// <summary> 构造时保存的策略默认值；显式注册覆盖仅影响当次执行通道，不改写这份配置快照。 </summary>
        private readonly Dictionary<ResourceSource, LoaderPolicy> _configuredLoaderPolicies = new Dictionary<ResourceSource, LoaderPolicy>();
        private readonly InstancePool? _instancePool;
        private readonly Func<CancellationToken, Task>[] _initializers;
        private readonly Func<Task>[] _shutdownActions;
        private readonly object _gate = new object();
        private Task? _initialization;
        private int _disposed;

        public ResourceSystem(ResourceSystemOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _context = options.ExecutionContext ?? throw new ArgumentException("缺少执行上下文", nameof(options));

            _store = new ResourceStore(_context, Diagnose);
            _converters = new Dictionary<Type, IProductConverter>(options.Converters);
            _shutdownActions = options.ShutdownActions.ToArray();
            foreach (var setup in options.Loaders.Values)
            {
                _configuredLoaderPolicies[setup.Source] = setup.Policy;
                if (setup.Loader != null) RegisterLoader(setup.Source, setup.Loader, setup.Policy);
            }
            _initializers = options.Initializers.ToArray();
            if (options.InstanceFactory != null)
            {
                _instancePool = new InstancePool(
                    options.InstanceFactory,
                    options.Pool,
                    (key, token) => AcquireRefAsync<object>(key, null, token),
                    _context);
            }
        }

        public bool IsInitialized
        {
            get { lock (_gate) return _initialization != null && _initialization.Status == TaskStatus.RanToCompletion; }
        }

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary> 执行全部准备工作 (如文件缓存启动清理)。同轮重复调用观察同一结果；失败或取消后再次调用会重试。 </summary>
        public Task InitializeAsync(CancellationToken token = default)
        {
            lock (_gate)
            {
                if (_initialization != null && !_initialization.IsFaulted && !_initialization.IsCanceled)
                    return _initialization;
                _initialization = InitializeCoreAsync(token);
                return _initialization;
            }
        }

        /// <summary>
        /// 注册或替换一个来源的加载器；初始化前后都可以调用 (AssetBundle 加载器通常在包系统就绪后才接入)。
        /// 未传策略时使用构造时的来源配置；来源未配置则用默认策略。显式策略只覆盖本次注册。
        /// 替换时旧通道在途的尝试照常完成，新请求与后续接续尝试走新通道；已缓存的产物不因替换失效。
        /// </summary>
        public void RegisterLoader(ResourceSource source, ILoader loader, LoaderPolicy? policy = null)
        {
            if (loader == null) throw new ArgumentNullException(nameof(loader));
            if (IsDisposed)
                throw new ResourceException(new ResourceError(DiagnosticCodes.LifecycleSystemDisposed, LoadStage.ValidateRequest));
            if (!policy.HasValue && _configuredLoaderPolicies.TryGetValue(source, out var configuredPolicy))
                policy = configuredPolicy;
            var lane = new SourceLane(loader, policy ?? LoaderPolicy.Default, _converters, _context, _store.KeepResidue);
            _store.SetLane(source, lane);
        }

        /// <summary> 取得资源的一份独立持有；同身份并发请求只进入一次实际加载 </summary>
        public Task<ResourceRef<T>> LoadAsync<T>(ResourceSource source, string address,
            IProgress<OperationProgress>? progress = null, CancellationToken token = default, object? parameters = null)
        {
            var key = new ResourceKey(source, address, typeof(T), parameters);
            return AcquireRefAsync<T>(key, progress, token);
        }

        /// <summary> 卸载业务选中且当前无人持有的产物；在用的跳过，失败的记为残留且不影响其他项 </summary>
        public Task<UnloadResult> UnloadAsync(Func<ResourceKey, bool>? selector = null)
        {
            EnsureOpen();
            return _store.UnloadAsync(selector);
        }

        /// <summary> 租一个实例 (原型按工厂的 PrototypeType 加载)；无闲置即创建，不排队 </summary>
        public Task<InstanceLease<T>> RentAsync<T>(ResourceSource source, string address,
            CancellationToken token = default, object? parameters = null)
        {
            EnsureOpen();
            if (_instancePool == null)
            {
                throw new ResourceException(new ResourceError(DiagnosticCodes.InstanceUnsupported, LoadStage.Instantiate,
                    new InvalidOperationException("装配时未提供 InstanceFactory")));
            }
            return _instancePool.RentAsync<T>(new ResourceKey(source, address, _instancePool.PrototypeType, parameters), token);
        }

        /// <summary> 关闭选中的池桶：销毁闲置实例，已有租约归还时销毁，对象全部结束后归还原型 </summary>
        public Task<PoolClearResult> ClearPoolAsync(Func<ResourceKey, bool>? selector = null)
        {
            EnsureOpen();
            return _instancePool != null ? _instancePool.ClearAsync(selector) : Task.FromResult(new PoolClearResult());
        }

        public ResourceSnapshot Snapshot()
        {
            var snapshot = new ResourceSnapshot();
            _store.Snapshot(snapshot.Entries, snapshot.Residues);
            _instancePool?.Snapshot(snapshot.Pools, snapshot.InstanceResidues);
            return snapshot;
        }

        /// <summary> 退出：先关池，再停止在途加载，最后卸载无人持有的产物并逆序执行关闭回调。可重复调用。 </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            List<Exception>? failures = null;
            try
            {
                if (_instancePool != null) await _instancePool.ClearAsync().ConfigureAwait(false);
                await _store.StopAsync().ConfigureAwait(false);
                await _store.UnloadAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failures = new List<Exception> { error };
            }

            for (var i = _shutdownActions.Length - 1; i >= 0; i--)
            {
                try { await _shutdownActions[i]().ConfigureAwait(false); }
                catch (Exception error)
                {
                    failures ??= new List<Exception>();
                    failures.Add(error);
                }
            }
            if (failures != null) throw new AggregateException("关闭资源系统失败。", failures);
        }

        private async Task InitializeCoreAsync(CancellationToken token)
        {
            foreach (var initializer in _initializers)
            {
                token.ThrowIfCancellationRequested();
                await initializer(token).ConfigureAwait(false);
            }
        }

        private async Task<ResourceRef<T>> AcquireRefAsync<T>(ResourceKey key, IProgress<OperationProgress>? progress, CancellationToken token)
        {
            EnsureOpen();
            var entry = await _store.AcquireAsync(key, progress, token).ConfigureAwait(false);
            return new ResourceRef<T>(_store, entry);
        }

        private void EnsureOpen()
        {
            if (IsDisposed)
                throw new ResourceException(new ResourceError(DiagnosticCodes.LifecycleSystemDisposed, LoadStage.ValidateRequest));
            if (!IsInitialized)
            {
                throw new ResourceException(new ResourceError(DiagnosticCodes.LifecycleManagerDisposed, LoadStage.ValidateRequest,
                    new InvalidOperationException("ResourceSystem 尚未初始化成功，请先 await InitializeAsync()")));
            }
        }

        private static void Diagnose(Exception error)
        {
            Log.Error("[ResourceSystem] " + error);
        }
    }
}
