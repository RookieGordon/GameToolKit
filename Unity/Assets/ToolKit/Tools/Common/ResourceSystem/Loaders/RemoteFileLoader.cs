/*
 * description  : 远程来源：先经文件缓存取得有效本地文件 (命中即不下载；同文件只下载一次)，再读出 byte[] 产物。
 *                不同目标类型 (如 byte[] 与 Texture2D) 是不同资源身份，但共享同一次文件填充；
 *                先到者取消只结束自己，填充在还有等待者时继续。传输与传输重试由已有下载组件承担。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Network;

namespace ToolKit.Tools.Common
{
    public sealed class RemoteFileLoader : ILoader
    {
        private readonly FileCache _cache;

        /// <param name="cache">文件缓存；由装配者创建与关闭，这里只借用</param>
        public RemoteFileLoader(FileCache cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        /// <summary> 文件缓存 + 文件读取都不需要引擎，忽略 context </summary>
        public async Task<IResource> LoadAsync(ResourceKey key, IExecutionContext context, IProgress<OperationProgress> progress, CancellationToken cancellation)
        {
            var path = await _cache.GetFileAsync(key.Address, progress, cancellation).ConfigureAwait(false);
            return new Product(await LocalFileLoader.ReadAllBytesAsync(path, key, progress, cancellation).ConfigureAwait(false));
        }

        /// <summary>
        /// 装配便利：注册远程来源，并把文件缓存的启动清理挂进系统初始化 (需求 Q02：启用文件缓存必须先清理)。
        /// 缓存仍由装配者创建与关闭。
        /// </summary>
        public static ResourceSystemOptions AddRemoteFiles(ResourceSystemOptions options, FileCache cache, LoaderPolicy? policy = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            options.Initializers.Add(cache.InitializeAsync);
            return options.AddLoader(ResourceSource.Remote, new RemoteFileLoader(cache), policy);
        }

        /// <summary>
        /// 用已有下载组件作为文件缓存的取得方式：下载到临时路径，完成/失败/取消映射为任务结果。
        /// 下载器由装配者创建、启动与释放；传输重试按下载器自己的配置。
        /// </summary>
        public static FileCache.Fetch UseDownloader(SimpleDownloader downloader, DownloadOptions options = null)
        {
            if (downloader == null) throw new ArgumentNullException(nameof(downloader));
            return async (url, temporaryPath, progress, token) =>
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = downloader.CreateTask(url, temporaryPath, null, options);
                task.OnCompleted = _ => completion.TrySetResult(true);
                task.OnCancelled = _ => completion.TrySetCanceled(token);
                task.OnFailed = (_, error) => completion.TrySetException(new ResourceException(new ResourceError(
                    _DiagnosticOf(error), LoadStage.Download, null,
                    new Dictionary<string, object> { { "url", url }, { "downloadError", error } })));
                if (progress != null)
                {
                    task.OnProgress = (_, value) => progress.Report(new OperationProgress(
                        value.TotalBytes > 0 ? Math.Min(1.0, (double)value.BytesDownloaded / value.TotalBytes) : (double?)null, "下载中"));
                }

                using (token.Register(task.Cancel))
                using (task)
                {
                    downloader.Start();                                    // 已运行时无操作
                    downloader.AddTask(task);
                    await completion.Task.ConfigureAwait(false);
                }
            };
        }

        private static string _DiagnosticOf(EDownloadError error)
        {
            switch (error)
            {
                case EDownloadError.InvalidUrl: return DiagnosticCodes.NetworkInvalidUri;
                case EDownloadError.Timeout: return DiagnosticCodes.NetworkTimeout;
                case EDownloadError.Network: return DiagnosticCodes.NetworkConnectFailed;
                case EDownloadError.Server: return DiagnosticCodes.NetworkHttpServerError;
                case EDownloadError.Storage: return DiagnosticCodes.CacheDiskFull;
                default: return DiagnosticCodes.NetworkInterrupted;
            }
        }
    }
}
