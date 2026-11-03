using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using ToolKit.Tools.Network;

namespace UnityToolKit.Runtime.ResourceSystem
{
    /// <summary>
    /// 配置创建的远程加载器，持有自己的文件缓存及可选下载器。
    /// 先等待 InitializeAsync，再注册或使用；外部注册时应在资源系统退出后 DisposeAsync。
    /// 外部提供的 Fetch 只被借用，其所有者不会在此处关闭。
    /// </summary>
    public sealed class ConfiguredRemoteFileLoader : ILoader, IAsyncDisposable
    {
        private readonly string _cacheDirectory;
        private readonly long _startupTargetBytes;
        private readonly TimeSpan? _maxAge;
        private readonly int _maxConcurrentDownloads;
        private readonly DownloadOptions _downloadOptions;
        private readonly FileCache.Fetch _fetch;
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        private Task _initialization;
        private Task _disposal;
        private FileCache _cache;
        private SimpleDownloader _downloader;
        private RemoteFileLoader _loader;

        internal ConfiguredRemoteFileLoader(string cacheDirectory, long startupTargetBytes,
            TimeSpan? maxAge, int maxConcurrentDownloads, DownloadOptions downloadOptions, FileCache.Fetch fetch = null)
        {
            _cacheDirectory = cacheDirectory;
            _startupTargetBytes = startupTargetBytes;
            _maxAge = maxAge;
            _maxConcurrentDownloads = maxConcurrentDownloads;
            _downloadOptions = downloadOptions;
            _fetch = fetch;
        }

        /// <summary>
        /// 同轮调用共享任务；成功后不重复清理，失败或取消后可重试。
        /// 首个调用的令牌控制该轮初始化，关闭加载器也会取消该轮。
        /// </summary>
        public Task InitializeAsync(CancellationToken token = default)
        {
            TaskCompletionSource<bool> completion;
            lock (_gate)
            {
                if (_disposal != null) throw new ObjectDisposedException(nameof(ConfiguredRemoteFileLoader));
                if (_initialization != null && !_initialization.IsFaulted && !_initialization.IsCanceled)
                    return _initialization;
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _initialization = completion.Task;
            }
            // 先发布任务再执行，关闭可以等待这一轮；外部操作及取消回调均不在锁内运行。
            _ = InitializeCoreAsync(completion, token);
            return completion.Task;
        }

        public Task<IResource> LoadAsync(ResourceKey key, IExecutionContext context,
            IProgress<OperationProgress> progress, CancellationToken cancellation)
        {
            RemoteFileLoader loader;
            lock (_gate)
            {
                if (_disposal != null) throw new ObjectDisposedException(nameof(ConfiguredRemoteFileLoader));
                if (_initialization == null || _initialization.Status != TaskStatus.RanToCompletion)
                    throw new InvalidOperationException("远程加载器尚未初始化成功，请先 await InitializeAsync()。");
                loader = _loader;
            }
            return loader.LoadAsync(key, context, progress, cancellation);
        }

        /// <summary> 取消并等待初始化，然后停止缓存填充，最后关闭自建下载器。重复调用共享同一关闭任务。 </summary>
        public ValueTask DisposeAsync()
        {
            Task initialization;
            TaskCompletionSource<bool> completion;
            lock (_gate)
            {
                if (_disposal != null) return new ValueTask(_disposal);
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposal = completion.Task;
                initialization = _initialization;
            }
            _ = DisposeCoreAsync(initialization, completion);
            return new ValueTask(completion.Task);
        }

        private async Task InitializeCoreAsync(TaskCompletionSource<bool> completion, CancellationToken token)
        {
            try
            {
                using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    CreateDependencies();
                    await _cache.InitializeAsync(cancellation.Token).ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    // UseDownloader 会在每次填充时调用 Start；先串行启动一次，避免首批并发填充同时启动。
                    _downloader?.Start();
                }
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException error)
            {
                completion.TrySetCanceled(error.CancellationToken);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }

        private void CreateDependencies()
        {
            // 只有当前初始化轮次会进入；重试复用之前已经创建的服务。
            if (_cache != null) return;
            SimpleDownloader downloader = null;
            var fetch = _fetch;
            if (fetch == null)
            {
                downloader = new SimpleDownloader(_maxConcurrentDownloads, _downloadOptions);
                fetch = RemoteFileLoader.UseDownloader(downloader);
            }
            var cache = new FileCache(new HashedPathConverter(_cacheDirectory), fetch, _startupTargetBytes, _maxAge);
            var loader = new RemoteFileLoader(cache);
            _cache = cache;
            _downloader = downloader;
            _loader = loader;
        }

        private async Task DisposeCoreAsync(Task initialization, TaskCompletionSource<bool> completion)
        {
            try
            {
                try
                {
                    _lifetime.Cancel();
                    if (initialization != null)
                    {
                        try { await initialization.ConfigureAwait(false); }
                        catch (Exception) { } // 初始化异常由原任务保留；不妨碍关闭已创建的依赖。
                    }
                    if (_cache != null) await _cache.StopAsync().ConfigureAwait(false);
                }
                finally
                {
                    try { _downloader?.Dispose(); }
                    finally { _lifetime.Dispose(); }
                }
                completion.TrySetResult(true);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }
    }
}
