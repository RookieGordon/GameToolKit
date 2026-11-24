/*
 * description  : 本地与远程文件来源加载器，以及内置文本转换器。
 *                本地来源异步读取字节流并映射 IO 诊断码；
 *                远程来源先经 FileCache 共享取得有效本地文件（不同目标表示共享同一次下载），再异步读出 byte[]。
 *                传输与传输重试由已有下载组件承担。
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Network;

namespace ToolKit.Tools.Common
{
    public sealed class LocalFileLoader : ILoader
    {
        private readonly string? _baseDirectory;

        /// <param name="baseDirectory">相对路径的基准目录；null 表示按当前工作目录</param>
        public LocalFileLoader(string? baseDirectory = null)
        {
            _baseDirectory = baseDirectory;
        }

        public string Resolve(string address)
        {
            return Path.IsPathRooted(address) || string.IsNullOrEmpty(_baseDirectory)
                ? Path.GetFullPath(address)
                : Path.GetFullPath(Path.Combine(_baseDirectory, address));
        }

        public async Task<IResource> LoadAsync(ResourceKey key, IProgress<OperationProgress>? progress, CancellationToken cancellation)
        {
            var path = Resolve(key.Address);
            if (!File.Exists(path)) throw ResourceException.Of(DiagnosticCodes.FileNotFound, LoadStage.ReadFile, key);
            return new Product(await ReadAllBytesAsync(path, key, progress, cancellation).ConfigureAwait(false));
        }

        internal static async Task<byte[]> ReadAllBytesAsync(string path, ResourceKey key, IProgress<OperationProgress>? progress, CancellationToken cancellation)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[stream.Length];
                    var offset = 0;
                    while (offset < buffer.Length)
                    {
                        var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellation).ConfigureAwait(false);
                        if (read == 0) throw new EndOfStreamException("文件在读取过程中被截短: " + path);
                        offset += read;
                        progress?.Report(new OperationProgress(buffer.Length == 0 ? 1 : (double)offset / buffer.Length, "读取文件"));
                    }
                    return buffer;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException error) { throw ResourceException.Of(DiagnosticCodes.FileAccessDenied, LoadStage.ReadFile, key, error); }
            catch (FileNotFoundException error) { throw ResourceException.Of(DiagnosticCodes.FileNotFound, LoadStage.ReadFile, key, error); }
            catch (DirectoryNotFoundException error) { throw ResourceException.Of(DiagnosticCodes.FileNotFound, LoadStage.ReadFile, key, error); }
            catch (IOException error) { throw ResourceException.Of(DiagnosticCodes.FileIoFailed, LoadStage.ReadFile, key, error); }
        }
    }

    public sealed class RemoteFileLoader : ILoader
    {
        private readonly FileCache _cache;

        /// <param name="cache">文件缓存；由装配者创建与关闭，这里只借用</param>
        public RemoteFileLoader(FileCache cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public async Task<IResource> LoadAsync(ResourceKey key, IProgress<OperationProgress>? progress, CancellationToken cancellation)
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
        public static FileCache.Fetch UseDownloader(SimpleDownloader downloader, DownloadOptions? options = null)
        {
            if (downloader == null) throw new ArgumentNullException(nameof(downloader));
            return async (url, temporaryPath, progress, token) =>
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = downloader.CreateTask(url, temporaryPath, null, options);
                task.OnCompleted = _ => completion.TrySetResult(true);
                task.OnCancelled = _ => completion.TrySetCanceled(token);
                task.OnFailed = (_, error) => completion.TrySetException(new ResourceException(new ResourceError(
                    DiagnosticOf(error), LoadStage.Download, null,
                    new Dictionary<string, object> { { "url", url }, { "downloadError", error } })));
                if (progress != null)
                {
                    task.OnProgress = (_, value) => progress.Report(new OperationProgress(
                        value.TotalBytes > 0 ? Math.Min(1.0, (double)value.BytesDownloaded / value.TotalBytes) : (double?)null, "下载中"));
                }

                using (token.Register(task.Cancel))
                using (task)
                {
                    downloader.Start();
                    downloader.AddTask(task);
                    await completion.Task.ConfigureAwait(false);
                }
            };
        }

        private static string DiagnosticOf(EDownloadError error)
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

    /// <summary> 内置转换：byte[] → string (默认 UTF-8)。注册到目标类型 string。 </summary>
    public sealed class TextConverter : IProductConverter
    {
        private readonly Encoding _encoding;

        public TextConverter(Encoding? encoding = null)
        {
            _encoding = encoding ?? new UTF8Encoding(false, true);
        }

        public Task<IResource> ConvertAsync(IResource source, ResourceKey key, CancellationToken cancellation)
        {
            if (!(source.Value is byte[] bytes))
                throw ResourceException.Of(DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, key);
            return Task.FromResult<IResource>(new Product(_encoding.GetString(bytes)));
        }
    }
}
