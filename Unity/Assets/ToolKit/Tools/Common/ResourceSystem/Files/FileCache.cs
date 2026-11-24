/*
 * description  : 远程内容的本地文件缓存。同一文件只有一个填充者 (SharedOperation 按 URL 共享)；
 *                写临时文件 → 关闭写入句柄 → 设置最后写入时间 (TTL 基准) → 改名发布。
 *                失败或被放弃的填充删除临时文件、不发布；最后一个等待者退出时取消传输，新请求等旧填充真正退出再开新一轮。
 *                TTL 只约束文件获取，不刷新内存产物；启动清理删除上次中断的临时文件并按目标容量删除最旧文件 (需求 Q02)。
 *                缓存目录由框架独占写入。创建者负责本对象的生命周期；资源系统只借用。
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public sealed class FileCache
    {
        /// <summary> 把 url 的内容取到 temporaryPath；传输重试归实现；token 是这一次填充的取消 </summary>
        public delegate Task Fetch(string url, string temporaryPath, IProgress<OperationProgress>? progress, CancellationToken token);

        private readonly IPathConverter _paths;
        private readonly Fetch _fetch;
        private readonly long _startupTargetBytes;
        private readonly TimeSpan? _maxAge;
        private readonly SharedOperation<string, string> _fills;

        /// <param name="paths">目录与路径映射</param>
        /// <param name="fetch">实际取得内容的方式 (如 RemoteFileLoader.UseDownloader)</param>
        /// <param name="startupTargetBytes">启动清理目标：初始化时把缓存目录删到不超过该大小；必须大于 0</param>
        /// <param name="maxAge">文件有效期；null 表示永不因年龄刷新 (仍受启动清理约束)</param>
        public FileCache(IPathConverter paths, Fetch fetch, long startupTargetBytes, TimeSpan? maxAge = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
            if (startupTargetBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(startupTargetBytes), "启用文件缓存必须给出有效的启动清理目标");
            if (maxAge.HasValue && maxAge.Value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxAge));
            if (string.IsNullOrEmpty(paths.RootDirectory)) throw new ArgumentException("缓存根目录不能为空", nameof(paths));
            _startupTargetBytes = startupTargetBytes;
            _maxAge = maxAge;
            _fills = new SharedOperation<string, string>(FillAsync, StringComparer.Ordinal, error => Log.Error("[FileCache] " + error));
        }

        public string RootDirectory => _paths.RootDirectory;

        /// <summary> 启动清理：删掉临时文件，再按最后写入时间从旧到新删到目标容量以内。放进 ResourceSystemOptions.Initializers。 </summary>
        public Task InitializeAsync(CancellationToken token = default)
        {
            return Task.Run(() =>
            {
                var root = new DirectoryInfo(RootDirectory);
                root.Create();
                foreach (var temporary in root.EnumerateFiles("*.tmp", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    temporary.Delete();
                }

                var files = root.EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(file => file.LastWriteTimeUtc).ToList();
                var total = files.Sum(file => file.Length);
                foreach (var file in files)
                {
                    if (total <= _startupTargetBytes) break;
                    token.ThrowIfCancellationRequested();
                    total -= file.Length;
                    file.Delete();
                }
            }, token);
        }

        /// <summary> 取得 url 对应的有效本地文件路径：新鲜缓存直接命中，否则加入或发起一次填充。token 只结束本人的等待。 </summary>
        public Task<string> GetFileAsync(string url, IProgress<OperationProgress>? progress, CancellationToken token)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("地址不能为空", nameof(url));
            var path = _paths.RemoteUrlToLocalPath(url);
            if (IsFresh(path)) return Task.FromResult(path);
            return _fills.GetAsync(url, progress, token);
        }

        /// <summary> 文件存在且 (没有有效期或) 自最后写入起未超过有效期 </summary>
        public bool IsFresh(string path)
        {
            if (!File.Exists(path)) return false;
            return !_maxAge.HasValue || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= _maxAge.Value;
        }

        /// <summary> 停止接收，取消在途填充并等它们真正退出 (临时文件随之删除) </summary>
        public Task StopAsync() => _fills.StopAsync();

        private async Task<string> FillAsync(string url, IProgress<OperationProgress> progress, CancellationToken token)
        {
            var path = _paths.RemoteUrlToLocalPath(url);
            if (IsFresh(path)) return path;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await _fetch(url, temporary, progress, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                File.SetLastWriteTimeUtc(temporary, DateTime.UtcNow);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
                return path;
            }
            catch (Exception error)
            {
                TryDelete(temporary);
                if (error is OperationCanceledException || error is ResourceException) throw;
                throw new ResourceException(new ResourceError(DiagnosticOf(error), LoadStage.Download, error,
                    new Dictionary<string, object> { { "url", url } }));
            }
        }

        private static string DiagnosticOf(Exception error)
        {
            if (error is UnauthorizedAccessException) return DiagnosticCodes.FileAccessDenied;
            if (error is IOException io && (io.HResult & 0xFFFF) == 0x70) return DiagnosticCodes.CacheDiskFull;
            if (error is IOException) return DiagnosticCodes.FileIoFailed;
            return DiagnosticCodes.NetworkInterrupted;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception error) { Log.Warn("[FileCache] 删除临时文件失败 " + path + ": " + error.Message); }
        }
    }

    /// <summary> 默认路径映射：根目录 + URL 的 SHA1 + 原扩展名 </summary>
    public sealed class HashedPathConverter : IPathConverter
    {
        public HashedPathConverter(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory)) throw new ArgumentException("根目录不能为空", nameof(rootDirectory));
            RootDirectory = Path.GetFullPath(rootDirectory);
        }

        public string RootDirectory { get; }

        public string RemoteUrlToLocalPath(string url)
        {
            string extension;
            try { extension = Path.GetExtension(new Uri(url).AbsolutePath); }
            catch (UriFormatException) { extension = string.Empty; }
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                var hash = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
                var name = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
                return Path.Combine(RootDirectory, name + extension);
            }
        }
    }
}
