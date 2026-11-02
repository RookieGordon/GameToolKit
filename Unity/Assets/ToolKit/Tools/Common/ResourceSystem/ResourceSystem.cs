/*
 * description  : 对外入口。装配时取配置快照；初始化成功后才开放加载；来源加载器可随时 RegisterLoader 注册或替换；
 *                并列组织各来源的 LoadManager、产物缓存与实例池；池只通过"取原型"一条窄路使用资源流程。
 *                退出顺序：关池 → 停止加载 → 卸载无人持有的产物；仍被业务持有的产物不强制释放，留在快照里。
 *
 * 主流程：LoadAsync → ResourceKey (身份) → LoadManager.AcquireAsync (命中缓存 / 合并等待 / 并发名额 / 一轮实际加载)
 *         → ResourceCache.Register (唯一产物) → ResourceRef (独立持有) → Dispose 归还 → UnloadAsync 业务选择卸载。
 */

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
        private readonly ResourceCache _cache;
        private readonly Dictionary<Type, IProductConverter> _converters;
        private readonly Dictionary<ResourceSource, LoadManager> _loadMgrDict = new Dictionary<ResourceSource, LoadManager>();
        /// <summary> 被替换下来的加载管理器：在途加载做完即止，退出时一起停止 </summary>
        private readonly List<LoadManager> _retiredLoadMgrs = new List<LoadManager>();
        private readonly InstancePool _instancePool;
        private readonly Func<CancellationToken, Task>[] _initializers;
        private readonly object _gate = new object();
        private Task _initialization;
        private int _disposed;

        public ResourceSystem(ResourceSystemOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _context = options.ExecutionContext ?? throw new ArgumentException("缺少执行上下文", nameof(options));

            // 独立快照：之后修改 options 不影响运行中的系统
            _cache = new ResourceCache(_context);
            _converters = new Dictionary<Type, IProductConverter>(options.Converters);
            foreach (var setup in options.Loaders.Values)
            {
                RegisterLoader(setup.Source, setup.Loader, setup.Policy);
            }
            _initializers = options.Initializers.ToArray();
            if (options.InstanceFactory != null)
            {
                // 池通过窄的"取原型"能力使用资源流程；资源流程不反过来管理池
                _instancePool = new InstancePool(options.InstanceFactory, options.Pool, _cache, (key, token) => AcquireAsync(key, null, token), _context);
            }
        }

        public bool IsInitialized
        {
            get { lock (_gate) return _initialization != null && _initialization.Status == TaskStatus.RanToCompletion; }
        }

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>
        /// 执行全部准备工作 (如文件缓存的启动清理)。同一轮内的重复调用观察同一结果；失败后再次调用会重试。
        /// </summary>
        public Task InitializeAsync(CancellationToken token = default)
        {
            lock (_gate)
            {
                if (_initialization != null && !_initialization.IsFaulted && !_initialization.IsCanceled) return _initialization;
                _initialization = InitializeAsyncInner(token);
                return _initialization;
            }
        }

        /// <summary>
        /// 注册或替换一个来源的加载器；初始化前后都可以调用 (AssetBundle 加载器通常在包系统就绪后才接入)。
        /// 替换时旧加载器在途的加载照常完成并进入缓存，新请求走新加载器；已缓存的产物不因替换而失效，需要时业务自行卸载。
        /// </summary>
        public void RegisterLoader(ResourceSource source, ILoader loader, LoaderPolicy? policy = null)
        {
            if (loader == null) throw new ArgumentNullException(nameof(loader));
            if (IsDisposed)
                throw new ResourceException(new ResourceError(DiagnosticCodes.LifecycleSystemDisposed, LoadStage.ValidateRequest));
            var manager = new LoadManager(loader, policy ?? LoaderPolicy.Default, _cache, _converters, _context, Diagnose);
            LoadManager replaced;
            lock (_gate)
            {
                _loadMgrDict.TryGetValue(source, out replaced);
                _loadMgrDict[source] = manager;
                if (replaced != null) _retiredLoadMgrs.Add(replaced);
            }
            if (replaced != null) _ = replaced.CompleteAsync();
        }

        /// <summary> 取得资源的一份独立持有；同身份并发请求只进入一次实际加载 </summary>
        public async Task<ResourceRef<T>> LoadAsync<T>(ResourceSource source, string address, object parameters = null,
            IProgress<OperationProgress> progress = null, CancellationToken token = default)
        {
            var key = new ResourceKey(source, address, typeof(T), parameters);
            var entry = await AcquireAsync(key, progress, token).ConfigureAwait(false);
            return new ResourceRef<T>(_cache, entry);
        }

        /// <summary> 预加载：加载成功后立即归还，产物留在缓存，下一次同身份请求直接命中 </summary>
        public async Task PreloadAsync<T>(ResourceSource source, string address, object parameters = null,
            IProgress<OperationProgress> progress = null, CancellationToken token = default)
        {
            var reference = await LoadAsync<T>(source, address, parameters, progress, token).ConfigureAwait(false);
            reference.Dispose();
        }

        /// <summary> 卸载业务选中且当前无人持有的产物；在用的跳过，失败的记为残留且不影响其他项 </summary>
        public Task<UnloadResult> UnloadAsync(Func<ResourceKey, bool> selector = null)
        {
            EnsureOpen();
            return _cache.UnloadAsync(selector);
        }

        /// <summary> 租一个实例 (原型按工厂的 PrototypeType 加载)；无闲置即创建，不排队 </summary>
        public Task<InstanceLease<T>> RentAsync<T>(ResourceSource source, string address, object parameters = null,
            CancellationToken token = default)
        {
            EnsureOpen();
            if (_instancePool == null)
                throw new ResourceException(new ResourceError(DiagnosticCodes.InstanceUnsupported, LoadStage.Instantiate,
                    new InvalidOperationException("装配时未提供 InstanceFactory")));
            return _instancePool.RentAsync<T>(new ResourceKey(source, address, _instancePool.PrototypeType, parameters), token);
        }

        /// <summary> 关闭选中的池桶：销毁闲置实例，已有租约归还时销毁，对象全部结束后归还原型 </summary>
        public Task<PoolClearResult> ClearPoolAsync(Func<ResourceKey, bool> selector = null)
        {
            EnsureOpen();
            return _instancePool != null ? _instancePool.ClearAsync(selector) : Task.FromResult(new PoolClearResult());
        }

        public ResourceSnapshot Snapshot()
        {
            var snapshot = new ResourceSnapshot();
            _cache.Snapshot(snapshot.Entries, snapshot.Residues);
            _instancePool?.Snapshot(snapshot.Pools, snapshot.InstanceResidues);
            return snapshot;
        }

        /// <summary> 退出：先关池，再停止加载，最后卸载无人持有的产物。可重复调用。 </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_instancePool != null) await _instancePool.ClearAsync().ConfigureAwait(false);
            List<LoadManager> managers;
            lock (_gate)
            {
                managers = new List<LoadManager>(_loadMgrDict.Values);
                managers.AddRange(_retiredLoadMgrs);
            }
            foreach (var manager in managers)
            {
                await manager.StopAsync().ConfigureAwait(false);
            }
            await _cache.UnloadAsync().ConfigureAwait(false);
        }

        private async Task InitializeAsyncInner(CancellationToken token)
        {
            foreach (var initializer in _initializers)
            {
                token.ThrowIfCancellationRequested();
                await initializer(token).ConfigureAwait(false);
            }
        }

        private Task<ResourceEntry> AcquireAsync(ResourceKey key, IProgress<OperationProgress> progress, CancellationToken token)
        {
            EnsureOpen();
            LoadManager loadManager;
            lock (_gate) _loadMgrDict.TryGetValue(key.Source, out loadManager);
            if (loadManager == null)
                throw ResourceException.Of(DiagnosticCodes.LoaderNotRegistered, LoadStage.Route, key);
            return loadManager.AcquireAsync(key, progress, token);
        }

        private void EnsureOpen()
        {
            if (IsDisposed)
                throw new ResourceException(new ResourceError(DiagnosticCodes.LifecycleSystemDisposed, LoadStage.ValidateRequest));
            if (!IsInitialized)
                throw new ResourceException(new ResourceError(DiagnosticCodes.LifecycleManagerDisposed, LoadStage.ValidateRequest,
                    new InvalidOperationException("ResourceSystem 尚未初始化成功，请先 await InitializeAsync()")));
        }

        private static void Diagnose(Exception error)
        {
            Log.Error("[ResourceSystem] " + error);
        }
    }
}
