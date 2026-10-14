/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 唯一业务入口与生命周期 (外观)。业务只需要 LoadAsync / RentAsync /
 *                UnloadUnusedAsync / ClearPoolAsync / Tick，不需要知道记录、等待者与并发额度。
 *                这里不承担定位与加载算法，全部转发给 ResourcePipeline 与 InstancePool。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public sealed class ResourceManager : IDisposable
    {
        private readonly IExecutionContext _context;
        private readonly ResourceSystemOptions _options;
        private readonly IResourceDiagnostics _diagnostics;
        private readonly ResourcePipeline _pipeline;
        private readonly FileCache? _fileCache;
        private readonly bool _ownsFileCache;
        private readonly TaskCompletionSource<bool> _initialization =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _initialized;
        private int _disposed;

        public ResourceManager(
            IExecutionContext? context = null,
            ResourceSystemOptions? options = null,
            IErrorMapper? errorMapper = null,
            IResourceDiagnostics? diagnostics = null,
            Func<double>? monotonicNow = null,
            FileCache? fileCache = null)
        {
            _context = context ?? ImmediateExecutionContext.Instance;
            _options = options ?? new ResourceSystemOptions();
            _options.Validate();
            _diagnostics = new SafeResourceDiagnostics(diagnostics);
            _ownsFileCache = fileCache == null;
            _fileCache = fileCache ?? new FileCache(new FileCacheOptions());
            _pipeline = new ResourcePipeline(
                _context, _options, _diagnostics, errorMapper ?? DefaultErrorMapper.Instance,
                monotonicNow ?? _DefaultClock, () => IsDisposed);
        }

        public bool IsInitialized => Volatile.Read(ref _initialized) != 0;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary> 本次加载使用的执行上下文；接入层需要编组线程时使用 </summary>
        public IExecutionContext Context => _context;

        #region 生命周期

        /// <summary>
        /// 启动入口。初始化幂等；configure 只执行一次，用于启动前注册或替换实现。
        /// 重复传入 configure 属于误用；初始化失败后不会开放业务入口。
        /// </summary>
        public Task InitializeAsync(Action<ResourceManager>? configure = null, CancellationToken cancellationToken = default)
        {
            _ThrowIfDisposed();
            if (configure != null)
            {
                if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
                {
                    throw new InvalidOperationException("资源管理器已经初始化，configure 只能执行一次");
                }
                return _InitializeAsync(configure, cancellationToken);
            }

            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            {
                return _initialization.Task;
            }
            return _InitializeAsync(null, cancellationToken);
        }

        private async Task _InitializeAsync(Action<ResourceManager>? configure, CancellationToken cancellationToken)
        {
            try
            {
                // 启动清理等准备动作先完成，再开放业务入口。
                await _fileCache!.InitializeAsync(cancellationToken).ConfigureAwait(false);
                configure?.Invoke(this);
                _initialization.TrySetResult(true);
            }
            catch (Exception error)
            {
                _initialization.TrySetException(error);
                // 初始化未成功：不保留"已经初始化"的假象，调用方可以修正后重试。
                Volatile.Write(ref _initialized, 0);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _initialization.TrySetResult(true);
            _ = _DisposeAsync();
        }

        private async Task _DisposeAsync()
        {
            try
            {
                await _pipeline.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _diagnostics.Report(new LoadError(DiagnosticCodes.InternalUnexpected, LoadStage.Dispose,
                    CleanupStatus.Unknown, error));
            }
            try
            {
                await _fileCache!.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _diagnostics.Report(new LoadError(DiagnosticCodes.FileIoFailed, LoadStage.Dispose,
                    CleanupStatus.Unknown, error));
            }
        }

        #endregion

        #region 注册与路由

        /// <summary> 注册来源的加载器；同一来源重复注册属于误用 </summary>
        public void RegisterLoader(ResourceSource source, IResourceLoader loader, LoaderPolicy? policy = null)
        {
            _ThrowIfDisposed();
            _pipeline.RegisterLoader(source, loader, policy);
        }

        /// <summary> 替换已注册来源的加载器；用于装配期或运行期切换实现 </summary>
        public void ReplaceLoader(ResourceSource source, IResourceLoader loader, LoaderPolicy? policy = null)
        {
            _ThrowIfDisposed();
            _pipeline.ReplaceLoader(source, loader, policy);
        }

        /// <summary> 注册实例工厂；同一名字重复注册属于误用 </summary>
        public void RegisterFactory(string name, IInstanceFactory factory, PoolPolicy? policy = null)
        {
            _ThrowIfDisposed();
            _pipeline.Pool.RegisterFactory(name, factory, policy);
        }

        #endregion

        #region 业务入口

        /// <summary> 取得资源引用；同资源的并发请求合并为一次实际加载 </summary>
        public Task<ResourceRef<T>> LoadAsync<T>(
            string address, ResourceSource source, RequestOptions? options = null,
            CancellationToken ct = default) where T : class
        {
            _ThrowIfNotUsable();
            return _pipeline.LoadAsync<T>(source, address, options, ct);
        }

        /// <summary> 租用实例；原型由注册的工厂决定，实例归还后回到池中 </summary>
        public Task<InstanceLease<T>> RentAsync<T>(
            string address, ResourceSource source, string? factory = null, RequestOptions? options = null,
            CancellationToken ct = default) where T : class
        {
            _ThrowIfNotUsable();
            return _pipeline.Pool.RentAsync<T>(
                factory ?? _options.DefaultFactory, address, source, options, ct);
        }

        /// <summary>
        /// 由业务决定何时、清理哪部分。只释放被 select 选中且当前无人持有的产物；
        /// 释放失败的具体残留随结果返回，不阻止其他项继续释放。
        /// </summary>
        public Task<ResourceUnloadResult> UnloadUnusedAsync(
            Predicate<ResourceRow>? select = null, CancellationToken ct = default)
        {
            _ThrowIfNotUsable();
            return _pipeline.UnloadUnusedAsync(select, ct);
        }

        /// <summary> 关闭指定地址的池桶并销毁其闲置实例；在用实例仍按各自租约归还 </summary>
        public Task ClearPoolAsync(
            string address, ResourceSource source, string? factory = null, CancellationToken ct = default)
        {
            _ThrowIfNotUsable();
            return _pipeline.Pool.ClearPoolAsync(factory ?? _options.DefaultFactory, address, source, ct);
        }

        /// <summary> 引擎帧或空闲时机调用：推进实例池的闲置维护 </summary>
        public void Tick()
        {
            if (IsDisposed) return;
            _pipeline.Tick();
        }

        public ResourceSnapshot GetSnapshot()
        {
            var snapshot = new ResourceSnapshot
            {
                IsInitialized = IsInitialized,
                IsDisposed = IsDisposed,
            };
            if (IsDisposed)
            {
                return snapshot;
            }
            snapshot.ResourceRows = _pipeline.SnapshotRows();
            snapshot.PoolRows = _context.Invoke(_pipeline.Pool.SnapshotRows);
            snapshot.DirtyResources = _pipeline.SnapshotDirtyResources();
            return snapshot;
        }

        #endregion

        private static double _DefaultClock()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        }

        private void _ThrowIfDisposed()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(ResourceManager));
        }

        private void _ThrowIfNotUsable()
        {
            _ThrowIfDisposed();
            if (!IsInitialized)
            {
                throw new InvalidOperationException("资源管理器尚未初始化，请先等待 InitializeAsync 完成");
            }
        }
    }
}
