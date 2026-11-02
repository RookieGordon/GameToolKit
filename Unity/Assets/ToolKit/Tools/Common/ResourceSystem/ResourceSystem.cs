/*
 * description  : 对外入口。装配时取配置快照；初始化成功后才开放加载；
 *                并列组织各来源的 LoadManager 与产物缓存 (实例池见 InstancePool)。
 *                退出顺序：停止加载 → 卸载无人持有的产物；仍被业务持有的产物不强制释放，留在快照里。
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
    }

    public sealed class ResourceSystem : IAsyncDisposable
    {
        private readonly IExecutionContext _context;
        private readonly ResourceCache _cache;
        private readonly Dictionary<ResourceSource, LoadManager> _sources = new Dictionary<ResourceSource, LoadManager>();
        private readonly Func<CancellationToken, Task>[] _initializers;
        private readonly object _gate = new object();
        private Task _initialization;
        private int _disposed;

        public ResourceSystem(ResourceSystemOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _context = options.ExecutionContext ?? throw new ArgumentException("缺少执行上下文", nameof(options));
            if (options.Loaders.Count == 0) throw new ArgumentException("至少注册一个来源的加载器", nameof(options));

            // 独立快照：之后修改 options 不影响运行中的系统
            _cache = new ResourceCache(_context);
            var converters = new Dictionary<Type, IProductConverter>(options.Converters);
            foreach (var setup in options.Loaders.Values)
            {
                _sources[setup.Source] = new LoadManager(setup.Loader, setup.Policy, _cache, converters, _context, _Diagnose);
            }
            _initializers = options.Initializers.ToArray();
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
                _initialization = _InitializeAsync(token);
                return _initialization;
            }
        }

        /// <summary> 取得资源的一份独立持有；同身份并发请求只进入一次实际加载 </summary>
        public async Task<ResourceRef<T>> LoadAsync<T>(ResourceSource source, string address, object parameters = null,
            IProgress<OperationProgress> progress = null, CancellationToken token = default)
        {
            var key = new ResourceKey(source, address, typeof(T), parameters);
            var entry = await _AcquireAsync(key, progress, token).ConfigureAwait(false);
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
            _EnsureOpen();
            return _cache.UnloadAsync(selector);
        }

        public ResourceSnapshot Snapshot()
        {
            var snapshot = new ResourceSnapshot();
            _cache.Snapshot(snapshot.Entries, snapshot.Residues);
            return snapshot;
        }

        /// <summary> 停止接收请求，取消在途加载，卸载无人持有的产物。可重复调用。 </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var manager in _sources.Values)
            {
                await manager.StopAsync().ConfigureAwait(false);
            }
            await _cache.UnloadAsync().ConfigureAwait(false);
        }

        private async Task _InitializeAsync(CancellationToken token)
        {
            foreach (var initializer in _initializers)
            {
                token.ThrowIfCancellationRequested();
                await initializer(token).ConfigureAwait(false);
            }
        }

        private Task<ResourceEntry> _AcquireAsync(ResourceKey key, IProgress<OperationProgress> progress, CancellationToken token)
        {
            _EnsureOpen();
            if (!_sources.TryGetValue(key.Source, out var manager))
                throw ResourceException.Of(DiagnosticCodes.LoaderNotRegistered, LoadStage.Route, key);
            return manager.AcquireAsync(key, progress, token);
        }

        private void _EnsureOpen()
        {
            if (IsDisposed)
                throw new ResourceException(new ResourceError(DiagnosticCodes.LifecycleSystemDisposed, LoadStage.ValidateRequest));
            if (!IsInitialized)
                throw new ResourceException(new ResourceError(DiagnosticCodes.LifecycleManagerDisposed, LoadStage.ValidateRequest,
                    new InvalidOperationException("ResourceSystem 尚未初始化成功，请先 await InitializeAsync()")));
        }

        private static void _Diagnose(Exception error)
        {
            Log.Error("[ResourceSystem] " + error);
        }
    }
}
