/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 本地文件缓存。阅读顺序：启动 → 取得文件 → 填充并发布 → 关闭。
 *                启动清理只执行一次；运行期只新增完整文件，不删除已交付的路径。
 *                fill 负责写完并关闭临时文件，缓存不知道内容来自下载、解压还是本地生成。
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
    public sealed class FileCache
    {
        private const string VersionDirectoryName = "http-cache-v2"; // 保留已发布的缓存目录格式
        private const string CacheFileSuffix = ".cache";
        private const string PartFileSuffix = ".part";

        private readonly string _directory;
        private readonly long? _startupTargetBytes;
        private readonly IResourceDiagnostics _diagnostics;
        private readonly Func<DateTime> _utcNow;
        private readonly object _gate = new object();

        private readonly Dictionary<string, CachedFile> _files =
            new Dictionary<string, CachedFile>(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingFileWrite> _pendingWrites =
            new Dictionary<string, PendingFileWrite>(StringComparer.Ordinal);
        private readonly Dictionary<string, FileValidity> _validityByKey =
            new Dictionary<string, FileValidity>(StringComparer.Ordinal);

        private readonly CancellationTokenSource _shutdownCancellation = new CancellationTokenSource();
        // null 表示尚未开始；开始后所有调用者等待同一任务，无需另一套 Started/State 标记。
        private Task? _initialization;
        private Task? _shutdown;

        public FileCache(FileCacheOptions options,
            IResourceDiagnostics? diagnostics = null,
            Func<DateTime>? utcNow = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options = options.Clone();
            options.Validate();
            _directory = Path.GetFullPath(Path.Combine(options.Directory, VersionDirectoryName));
            _startupTargetBytes = options.StartupTargetBytes;
            _diagnostics = new SafeResourceDiagnostics(diagnostics ?? NullResourceDiagnostics.Instance);
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// 在开放资源入口前调用。后台执行一次扫描与清理；重复调用只等待，不再次清理。
        /// 同一目录在一次应用运行中只由一个缓存实例初始化，避免清理仍在使用的路径。
        /// 调用者取消只停止自己的等待。
        /// </summary>
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_shutdown != null)
                    throw new InvalidOperationException("缓存已关闭，不能再初始化");

                // 在锁内发布唯一任务；磁盘扫描在工作线程执行，不占用调用线程和记录表锁。
                _initialization ??= Task.Run(_Initialize);
                return _initialization.WaitWithCancellation(cancellationToken);
            }
        }

        /// <summary>
        /// 有效本地文件直接返回；未命中时，同一身份共享一次 fill。
        /// fill 必须在结束实际 I/O、关闭写入句柄后才完成。调用者取消不取消共享填充。
        /// 返回路径在本次应用运行期间保持有效，不需要释放。
        /// </summary>
        public async Task<string> GetOrCreateAsync(
            FileRequest request,
            Func<string, CancellationToken, Task> fill,
            CancellationToken cancellationToken = default)
        {
            if (fill == null) throw new ArgumentNullException(nameof(fill));
            _ValidateRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            Task initialization;
            lock (_gate)
            {
                _ThrowIfClosing();
                initialization = _initialization ?? throw new InvalidOperationException(
                    "FileCache 尚未初始化：必须在资源加载前调用 InitializeAsync");
            }
            await initialization.WaitWithCancellation(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var key = FileKeyEncoding.Encode(request.Identity);
            CachedFile? cachedFile = null;
            PendingFileWrite? writeToStart = null;
            Task<string>? fileReady = null;
            lock (_gate)
            {
                _ThrowIfClosing();
                _RegisterValidity(key, request);
                if (_files.TryGetValue(key, out var file) && _CanReuse(file, request.Validity))
                {
                    cachedFile = file;
                }
                else if (_pendingWrites.TryGetValue(key, out var pending))
                {
                    _CheckSharedWrite(pending.Request, request);
                    fileReady = pending.Completion.Task;
                }
                else
                {
                    writeToStart = new PendingFileWrite(request);
                    _pendingWrites.Add(key, writeToStart);
                    fileReady = writeToStart.Completion.Task;
                }
            }

            if (cachedFile != null)
            {
                try
                {
                    await _ValidateFileAsync(cachedFile.Path, request, cancellationToken).ConfigureAwait(false);
                    return cachedFile.Path;
                }
                catch (ResourceLoadException ex) when (ex.Error.DiagnosticCode == DiagnosticCodes.CacheIntegrityFailed)
                {
                    // 本次报告校验失败，下次请求可重新填充；已交付的旧路径不删除。
                    lock (_gate)
                    {
                        if (_files.TryGetValue(key, out var current) && ReferenceEquals(current, cachedFile))
                            _files.Remove(key);
                    }
                    throw;
                }
            }

            // 先登记再启动，且 fill 的同步部分也在锁外执行。
            if (writeToStart != null)
                _ = _FillAndPublishAsync(key, writeToStart, fill);

            await fileReady!.WaitWithCancellation(cancellationToken).ConfigureAwait(false);
            var path = await fileReady.ConfigureAwait(false);
            // 加入者可能有首个请求未声明的长度/摘要要求，每个调用者仍需独立校验。
            await _ValidateFileAsync(path, request, cancellationToken).ConfigureAwait(false);
            return path;
        }

        /// <summary>
        /// 拒绝新请求，取消并等待实际填充及临时文件清理。不删除完整文件、不等待路径使用者。
        /// 取消某次等待不撤销关闭，后续调用仍等待同一次关闭。
        /// </summary>
        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                // 先登记关闭任务以拒绝新请求；取消回调和异步收尾均在锁外执行。
                _shutdown ??= Task.Run(_ShutdownAsync);
                return _shutdown.WaitWithCancellation(cancellationToken);
            }
        }

        private void _ThrowIfClosing()
        {
            if (_shutdown != null)
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LifecycleSystemClosing, LoadStage.CacheLookup, CleanupStatus.Complete));
        }

        #region 启动：扫描 → 删除残留/旧副本 → 按目标清理 → 建立索引

        private void _Initialize()
        {
            var token = _shutdownCancellation.Token;
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_directory);
            var files = _ScanCompletedFiles(token);
            var latestFiles = new Dictionary<string, CachedFile>(StringComparer.Ordinal);
            var totalBytes = files.Sum(file => file.Length);

            foreach (var file in files.OrderByDescending(file => file.CompletedAtUtc))
            {
                token.ThrowIfCancellationRequested();
                if (!latestFiles.ContainsKey(file.Key))
                    latestFiles.Add(file.Key, file);
                else if (_TryDeleteFile(file.Path, "startup-old-copy"))
                    totalBytes -= file.Length;
            }

            if (_startupTargetBytes is long target)
            {
                foreach (var file in latestFiles.Values.OrderBy(file => file.CompletedAtUtc).ToList())
                {
                    token.ThrowIfCancellationRequested();
                    if (totalBytes <= target) break;
                    if (_TryDeleteFile(file.Path, "startup-target"))
                    {
                        totalBytes -= file.Length;
                        latestFiles.Remove(file.Key);
                    }
                }
            }

            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                foreach (var file in latestFiles.Values)
                    _files.Add(file.Key, file);
            }
        }

        private List<CachedFile> _ScanCompletedFiles(CancellationToken token)
        {
            var files = new List<CachedFile>();
            // 根目录无法枚举属于初始化失败；单个文件读不出则记录并跳过。
            foreach (var path in Directory.GetFiles(_directory))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    if (info.Name.EndsWith(PartFileSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        var stem = Path.GetFileNameWithoutExtension(info.Name);
                        if (Guid.TryParseExact(stem, "N", out _))
                            _TryDeleteFile(path, "startup-part");
                    }
                    else if (info.Name.EndsWith(CacheFileSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        var file = _ParseCompletedFile(info);
                        if (file != null)
                            files.Add(file);
                        else
                            _ReportFileIssue(DiagnosticCodes.CacheIndexFailed, LoadStage.CacheLookup,
                                path, "unrecognized-name");
                    }
                }
                catch (Exception ex)
                {
                    _ReportFileIssue(DiagnosticCodes.CacheIndexFailed, LoadStage.CacheLookup,
                        path, "scan-file", ex);
                }
            }
            return files;
        }

        private static CachedFile? _ParseCompletedFile(FileInfo file)
        {
            // <keyHash>_<completedUtcTicks>_<randomId>.cache
            var parts = Path.GetFileNameWithoutExtension(file.Name).Split('_');
            if (parts.Length != 3 || !_IsSha256(parts[0])
                || !long.TryParse(parts[1], out var ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks
                || !Guid.TryParseExact(parts[2], "N", out _))
                return null;

            return new CachedFile(parts[0].ToLowerInvariant(), file.FullName,
                new DateTime(ticks, DateTimeKind.Utc), file.Length);
        }

        #endregion

        #region 填充：写临时文件 → 校验 → 发布 → 清理 → 通知所有等待者

        private async Task _FillAndPublishAsync(
            string key, PendingFileWrite pending, Func<string, CancellationToken, Task> fill)
        {
            var temporaryPath = Path.Combine(_directory, Guid.NewGuid().ToString("N") + PartFileSuffix);
            string? publishedPath = null;
            Exception? failure = null;
            try
            {
                var token = _shutdownCancellation.Token;
                token.ThrowIfCancellationRequested();
                await fill(temporaryPath, token).ConfigureAwait(false);
                var length = await _ValidateFileAsync(temporaryPath, pending.Request, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var completedAt = _utcNow();
                var finalPath = Path.Combine(_directory,
                    $"{key}_{completedAt.Ticks:0}_{Guid.NewGuid():N}{CacheFileSuffix}");
                File.Move(temporaryPath, finalPath);
                // 发布后不再回删，即使此时关闭；旧路径留到下次启动再清理。
                lock (_gate)
                {
                    _files[key] = new CachedFile(key, finalPath, completedAt, length);
                }
                publishedPath = finalPath;
            }
            catch (Exception ex)
            {
                failure = ex is IOException || ex is UnauthorizedAccessException
                    ? _FileError(temporaryPath, ex, LoadStage.ValidateContent)
                    : ex;
                if (failure is not OperationCanceledException)
                {
                    _diagnostics.Report(failure is ResourceLoadException resourceError ? resourceError.Error
                        : new LoadError(DiagnosticCodes.InternalUnexpected, LoadStage.WaitForLoad,
                            CleanupStatus.Complete, failure,
                            new Dictionary<string, object> { { "key", key } }));
                }
            }
            finally
            {
                _TryDeleteFile(temporaryPath, "fill-cleanup");
                lock (_gate)
                {
                    // 共享结果即实际收尾完成，关闭不必再追踪另一份“执行任务”。
                    _pendingWrites.Remove(key);
                    if (failure is OperationCanceledException)
                        pending.Completion.TrySetCanceled();
                    else if (failure != null)
                    {
                        pending.Completion.TrySetException(failure);
                        _ = pending.Completion.Task.Exception; // 所有调用者取消后，故障仍被观察。
                    }
                    else
                        pending.Completion.TrySetResult(publishedPath!);
                }
            }
        }

        private bool _CanReuse(CachedFile file, FileValidity validity)
        {
            return (validity.Mode != ValidityMode.ExpiresAfter
                    || _utcNow() - file.CompletedAtUtc <= validity.Ttl)
                   && File.Exists(file.Path);
        }

        private void _RegisterValidity(string key, FileRequest request)
        {
            if (_validityByKey.TryGetValue(key, out var validity)
                && !validity.IsCompatibleWith(request.Validity))
                throw _IdentityConflict(request);
            _validityByKey[key] = request.Validity;
        }

        private static void _CheckSharedWrite(FileRequest first, FileRequest incoming)
        {
            if (first.ExpectedLength.HasValue && incoming.ExpectedLength.HasValue
                && first.ExpectedLength != incoming.ExpectedLength)
                throw _IdentityConflict(incoming);
            if (first.ExpectedSha256 != null && incoming.ExpectedSha256 != null
                && !string.Equals(first.ExpectedSha256, incoming.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw _IdentityConflict(incoming);
        }

        #endregion

        #region 校验：发布前与交付前使用同一规则，仅在声明摘要时计算 SHA

        private static void _ValidateRequest(FileRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.ExpectedLength < 0)
                throw new ArgumentException("ExpectedLength 不能为负");
            if (request.ExpectedSha256 != null && !_IsSha256(request.ExpectedSha256))
                throw new ArgumentException("ExpectedSha256 必须是 64 位十六进制摘要");
        }

        private static async Task<long> _ValidateFileAsync(
            string path, FileRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            long length;
            string? digest = null;
            try
            {
                length = new FileInfo(path).Length;
                if (request.ExpectedSha256 != null)
                    digest = await _ComputeSha256Async(path, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw _FileError(path, ex, LoadStage.ReadFile);
            }

            if (request.ExpectedLength is long expected && length != expected)
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.CacheIntegrityFailed, LoadStage.ValidateContent, CleanupStatus.Complete,
                    null, new Dictionary<string, object>
                    {
                        { "path", path }, { "expected", expected }, { "actual", length },
                    }));
            if (request.ExpectedSha256 != null
                && !string.Equals(request.ExpectedSha256, digest, StringComparison.OrdinalIgnoreCase))
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.CacheIntegrityFailed, LoadStage.ValidateContent, CleanupStatus.Complete,
                    null, new Dictionary<string, object> { { "path", path }, { "reason", "sha256-mismatch" } }));

            token.ThrowIfCancellationRequested();
            return length;
        }

        private static async Task<string> _ComputeSha256Async(string path, CancellationToken token)
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, useAsync: true);
            var buffer = new byte[81920];
            int count;
            // 使用 Unity/netstandard2.1 可用的流式接口，不把整个文件读进内存。
            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                sha.TransformBlock(buffer, 0, count, buffer, 0);
            token.ThrowIfCancellationRequested();
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var hex = new StringBuilder(64);
            foreach (var value in sha.Hash!)
                hex.Append(value.ToString("x2"));
            return hex.ToString();
        }

        private static bool _IsSha256(string text)
        {
            return text.Length == 64 && text.All(character =>
                character >= '0' && character <= '9'
                || character >= 'a' && character <= 'f'
                || character >= 'A' && character <= 'F');
        }

        #endregion

        #region 关闭与本地文件诊断

        private async Task _ShutdownAsync()
        {
            List<Task> unfinished;
            lock (_gate)
            {
                unfinished = _pendingWrites.Values.Select(pending => (Task)pending.Completion.Task).ToList();
                if (_initialization != null) unfinished.Add(_initialization);
            }

            try
            {
                try
                {
                    _shutdownCancellation.Cancel();
                }
                catch (Exception ex)
                {
                    // 回调异常不应跳过其他填充的等待，更不能提前释放生命周期令牌。
                    _ReportFileIssue(DiagnosticCodes.InternalUnexpected, LoadStage.Shutdown,
                        _directory, "cancel-fill", ex);
                }
                try
                {
                    await Task.WhenAll(unfinished).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 关闭等待的是实际结束；填充/初始化的失败仍由各自原任务交付。
                }
            }
            finally
            {
                _shutdownCancellation.Dispose();
            }
        }

        private bool _TryDeleteFile(string path, string reason)
        {
            try
            {
                File.Delete(path); // 文件已不存在也算清理完成。
                return true;
            }
            catch (Exception ex)
            {
                _ReportFileIssue(DiagnosticCodes.FileIoFailed, LoadStage.CacheEvict, path, reason, ex);
                return false;
            }
        }

        private void _ReportFileIssue(string code, LoadStage stage, string path, string reason, Exception? error = null)
        {
            _diagnostics.Report(new LoadError(code, stage, CleanupStatus.Complete, error,
                new Dictionary<string, object> { { "file", Path.GetFileName(path) }, { "reason", reason } }));
        }

        private static ResourceLoadException _IdentityConflict(FileRequest request)
        {
            return new ResourceLoadException(new LoadError(
                DiagnosticCodes.CacheIdentityConflict, LoadStage.CacheLookup, CleanupStatus.Complete,
                null, new Dictionary<string, object> { { "identity", request.Identity.ToString() } }));
        }

        private static ResourceLoadException _FileError(string path, Exception error, LoadStage stage)
        {
            var code = error is FileNotFoundException || error is DirectoryNotFoundException
                ? DiagnosticCodes.FileNotFound
                : FileSystemErrorClassifier.IsDiskFull(error) ? DiagnosticCodes.CacheDiskFull
                : FileSystemErrorClassifier.IsAccessDenied(error) ? DiagnosticCodes.FileAccessDenied
                : DiagnosticCodes.FileIoFailed;
            return new ResourceLoadException(new LoadError(code, stage, CleanupStatus.Complete, error,
                new Dictionary<string, object> { { "path", path } }));
        }

        #endregion

        // 普通文件记录和一次填充所需的数据；不承载额外的文件生命周期状态机。
        private sealed class CachedFile
        {
            public readonly string Key;
            public readonly string Path;
            public readonly DateTime CompletedAtUtc;
            public readonly long Length;

            public CachedFile(string key, string path, DateTime completedAtUtc, long length)
            {
                Key = key;
                Path = path;
                CompletedAtUtc = completedAtUtc;
                Length = length;
            }
        }

        private sealed class PendingFileWrite
        {
            public readonly FileRequest Request;
            public readonly TaskCompletionSource<string> Completion =
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            public PendingFileWrite(FileRequest request)
            {
                Request = request;
            }
        }
    }
}
