/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 文件填充：临时文件 → 校验 → 发布，同一身份只填充一次，相同身份的并发请求等待同一次结果。
 *                运行期不删除已经交付的路径；初始化时按目标容量做一次启动清理并删除残留临时文件。
 *                这里不理解 HTTP，也不解析资源地址：取回内容由注入的 fill 完成。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 把内容写入缓存分配的临时路径；实现不得直接写最终路径 </summary>
    public delegate Task FileFill(string temporaryPath, CancellationToken cancellationToken);

    /// <summary>
    /// 文件缓存 (公开类型名保留 FileCache)。公开扩展点，可由构造参数注入并共享给业务下载流程。
    /// </summary>
    public sealed class FileCache : IDisposable
    {
        private const string TemporarySuffix = ".tmp";

        private readonly FileCacheOptions _options;
        private readonly Dictionary<string, InflightFill> _inflight = new Dictionary<string, InflightFill>();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly string _root;
        private int _initialized;
        private int _disposed;

        public FileCache(FileCacheOptions? options = null)
        {
            _options = options ?? new FileCacheOptions();
            _root = string.IsNullOrEmpty(_options.Directory)
                ? Path.Combine(Path.GetTempPath(), "toolkit-resource-cache")
                : _options.Directory!;
        }

        public string Root => _root;

        /// <summary> 建立缓存目录并做一次启动清理；重复调用只执行一次 </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
            try
            {
                Directory.CreateDirectory(_root);
                await _StartupCleanupAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Volatile.Write(ref _initialized, 0);
                throw;
            }
        }

        /// <summary>
        /// 取得已发布的文件路径：命中有效文件直接返回；否则由 fill 写入临时文件，成功发布后返回最终路径。
        /// 同一身份的并发请求共享一次填充；填充失败不发布、不占用最终路径。
        /// </summary>
        public async Task<string> GetOrCreateAsync(
            FileRequest request, FileFill fill, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (fill == null) throw new ArgumentNullException(nameof(fill));
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(FileCache));

            var key = _ComposeKey(request.Identity);
            var path = Path.Combine(_root, key + ".bin");
            if (IsUsable(path) && !request.Validity.IsExpired(DateTimeOffset.UtcNow)) return path;

            Task<string> shared;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_inflight.TryGetValue(key, out var existing))
                {
                    shared = existing.Completion.Task;
                }
                else
                {
                    var created = new InflightFill(key, path);
                    _inflight.Add(key, created);
                    shared = created.Completion.Task;
                    _ = _FillAndPublishAsync(created, fill, cancellationToken);
                }
            }
            finally
            {
                _gate.Release();
            }

            return await shared.ConfigureAwait(false);
        }

        private async Task _FillAndPublishAsync(InflightFill entry, FileFill fill, CancellationToken cancellationToken)
        {
            var temporary = entry.Path + "." + Guid.NewGuid().ToString("N") + TemporarySuffix;
            try
            {
                await fill(temporary, cancellationToken).ConfigureAwait(false);
                if (!File.Exists(temporary))
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.FileNotFound, LoadStage.Download, CleanupStatus.Complete, null,
                        new Dictionary<string, object> { { "path", temporary } }));
                }
                _Publish(temporary, entry.Path);
                entry.Completion.TrySetResult(entry.Path);
            }
            catch (Exception error)
            {
                _TryDelete(temporary);
                entry.Completion.TrySetException(_AsError(error, entry.Path));
            }
            finally
            {
                // 收尾时必须摘掉登记项：否则后来的请求会一直等到一个已经结束的填充。
                await _gate.WaitAsync().ConfigureAwait(false);
                try { _inflight.Remove(entry.Key); }
                finally { _gate.Release(); }
            }
        }

        private static void _Publish(string temporary, string finalPath)
        {
            try
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(temporary, finalPath);
            }
            catch (Exception error)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.FileIoFailed, LoadStage.Download, CleanupStatus.Complete, error,
                    new Dictionary<string, object> { { "path", finalPath } }));
            }
        }

        private static Exception _AsError(Exception error, string path)
        {
            if (error is ResourceLoadException) return error;
            var diagnostic = _IsDiskFull(error)
                ? DiagnosticCodes.CacheDiskFull
                : DiagnosticCodes.FileIoFailed;
            return new ResourceLoadException(new LoadError(
                diagnostic, LoadStage.Download, CleanupStatus.Unknown, error,
                new Dictionary<string, object> { { "path", path } }));
        }

        private static bool _IsDiskFull(Exception error)
        {
            // 磁盘满与普通 I/O 失败分开映射；证据不足时不猜测具体原因。
            return error is IOException
                   && (error.HResult == unchecked((int)0x80070070)
                       || error.HResult == unchecked((int)0x8007006F));
        }

        public async Task DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _inflight.Clear();
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _gate.Dispose();
        }

        #region 启动清理与校验

        private async Task _StartupCleanupAsync(CancellationToken cancellationToken)
        {
            var target = _options.StartupTargetBytes;
            var files = new DirectoryInfo(_root).GetFiles("*", SearchOption.TopDirectoryOnly);
            long total = 0;
            var published = new List<FileInfo>();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Name.EndsWith(TemporarySuffix, StringComparison.Ordinal))
                {
                    _TryDelete(file.FullName); // 上次进程中断留下的临时文件
                    continue;
                }
                total += file.Length;
                published.Add(file);
            }
            if (target is not long limit || total <= limit) return;

            foreach (var file in published.OrderBy(f => f.LastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (total <= limit) return;
                var length = file.Length;
                if (_TryDelete(file.FullName)) total -= length;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static bool IsUsable(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length > 0; }
            catch (IOException) { return false; }
        }

        private static bool _TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        #endregion

        private static string _ComposeKey(FileIdentity identity)
        {
            return _ComposeKey(identity.Source + "\n" + identity.Path + "\n" + identity.Revision + "\n" + identity.Variant);
        }

        private static string _ComposeKey(string material)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        /// <summary> 同一身份的一次共享填充；相同身份的并发请求都等这一个结果 </summary>
        private sealed class InflightFill
        {
            internal InflightFill(string key, string path)
            {
                Key = key;
                Path = path;
                Completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            /// <summary> 登记用的身份键；收尾时按它摘除，不靠文件路径反推 </summary>
            internal string Key { get; }

            internal string Path { get; }

            internal TaskCompletionSource<string> Completion { get; }
        }
    }
}
