/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 下载文件缓存 (删减版)。本地有有效文件就复用，没有就下载；
 *                应用启动前清理一次 (.part 残留、同 key 旧副本、超启动目标的旧文件)，
 *                运行期间不自动清理已完成文件，不承诺磁盘容量硬上限。
 *                已返回的路径在本次运行内不被本组件删除或覆盖，调用者无须租约。
 *                文件命名自带缓存键与下载完成时间：<keyHash>_<completedUtcTicks>_<randomId>.cache。
 *                取消语义放宽：调用者取消只停止自己的等待；共享下载使用缓存自身生命周期令牌，
 *                即使所有调用者都取消，已启动的下载仍可完成并留作缓存。
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
    /// <summary> 运行期普通文件记录：不含 Pin/Generation/Retired/DeleteRetry 等生命周期字段 </summary>
    internal sealed class CachedRecord
    {
        public readonly string Key;
        public readonly string Path;
        public readonly DateTime CompletedAtUtc;
        public readonly long Length;

        public CachedRecord(string key, string path, DateTime completedAtUtc, long length)
        {
            Key = key;
            Path = path;
            CompletedAtUtc = completedAtUtc;
            Length = length;
        }
    }

    public sealed class FileCache
    {
        private const string VersionDirectoryName = "http-cache-v2";
        private const string CacheFileSuffix = ".cache";
        private const string PartFileSuffix = ".part";

        private enum FileCacheState
        {
            NotInitialized,
            Initializing,
            Open,
            Closing,
            Closed,
        }

        private readonly FileCacheOptions _options;          // 构造时验证并复制的冻结快照
        private readonly NetworkOptions _network;            // 冻结快照
        private readonly IFileTransport _transport;          // 装配根拥有，缓存不释放
        private readonly IResourceDiagnostics _diagnostics;
        private readonly Func<DateTime> _utcNow;

        private readonly object _gate = new object();        // 短同步临界区：记录表/共享任务表/状态
        private readonly Dictionary<string, CachedRecord> _records =
            new Dictionary<string, CachedRecord>(StringComparer.Ordinal);

        private readonly Dictionary<string, Task<string>> _downloads =
            new Dictionary<string, Task<string>>(StringComparer.Ordinal);

        /// <summary> 共享任务发起请求的声明 (长度/摘要)，用于加入时的身份冲突检查 </summary>
        private readonly Dictionary<Task<string>, (long? Length, string? Sha256)> _activeRequestClaims =
            new Dictionary<Task<string>, (long?, string?)>();

        private readonly Dictionary<FileIdentity, FileValidity> _establishedValidities =
            new Dictionary<FileIdentity, FileValidity>();

        private readonly SemaphoreSlim _downloadSlots;
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();

        private FileCacheState _state = FileCacheState.NotInitialized;
        private Task _initializeTask = Task.CompletedTask;
        private Task _shutdownTask = Task.CompletedTask;
        private int _initializeStarted;
        private int _shutdownStarted;

        public FileCache(FileCacheOptions options, IFileTransport transport,
            NetworkOptions? network = null, IResourceDiagnostics? diagnostics = null,
            Func<DateTime>? utcNow = null)
        {
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Clone();
            _options.Validate();
            _network = (network ?? new NetworkOptions()).Clone();
            _network.Validate();
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _diagnostics = new SafeResourceDiagnostics(diagnostics ?? NullResourceDiagnostics.Instance);
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            var slots = Math.Max(1, _network.MaxConcurrentDownloads);
            _downloadSlots = new SemaphoreSlim(slots, slots);
        }

        #region 初始化 (启动清理，只执行一次)

        /// <summary>
        /// 启动次序：创建专用版本子目录 → 枚举受管文件 → 尽力删 .part → 同 key 留最新 →
        /// 超启动目标按完成时间从旧到新删除 → 建立内存字典。删除失败记录诊断并继续；
        /// 只有无法创建缓存根目录等基础故障才失败。取消等待不撤销已开始的初始化；
        /// 重复调用只等待同一个初始化任务；GetFileAsync 不触发首次清理。
        /// </summary>
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _initializeStarted, 1) == 0)
            {
                _initializeTask = _InitializeCoreAsync();
            }
            return _initializeTask.WaitWithCancellation(cancellationToken);
        }

        private async Task _InitializeCoreAsync()
        {
            lock (_gate)
            {
                if (_state == FileCacheState.Closing || _state == FileCacheState.Closed)
                {
                    throw new InvalidOperationException("缓存已关闭，不能再初始化");
                }
                _state = FileCacheState.Initializing;
            }

            try
            {
                var directory = _CacheDirectory();
                Directory.CreateDirectory(directory); // 无法创建根目录等基础故障使初始化失败
                await _StartupCleanupAsync(directory).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_lifetimeCts.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(_lifetimeCts.Token);
                    }
                    _state = FileCacheState.Open;
                }
            }
            catch
            {
                lock (_gate)
                {
                    _state = FileCacheState.Closed; // 初始化失败不开放 Get，也不自动重试清理
                }
                throw;
            }
        }

        private string _CacheDirectory()
        {
            return Path.Combine(_options.Directory, VersionDirectoryName);
        }

        /// <summary> 扫描过程中的总量是本次清理的局部变量，不保留为运行期容量账本 </summary>
        private Task _StartupCleanupAsync(string directory)
        {
            // 1. 枚举受管文件；未知文件/目录跳过不删，不跟随链接
            var completeFiles = new List<(string Path, string Key, DateTime CompletedAtUtc, long Length)>();
            foreach (var file in _ListFilesSafe(directory))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.EndsWith(PartFileSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    _TryDeleteFile(file, "startup-part"); // 尽力删除残留 .part，不受 StartupTarget 影响
                    continue;
                }
                if (!fileName.EndsWith(CacheFileSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 未知文件不自动删除
                }
                var parsed = _ParseCacheFileName(fileName);
                if (parsed == null)
                {
                    _diagnostics.Report(new LoadError(
                        DiagnosticCodes.CacheIndexFailed, LoadStage.CacheLookup, CleanupStatus.Complete, null,
                        new Dictionary<string, object> { { "file", fileName }, { "reason", "unrecognized-name" } }));
                    continue;
                }
                long length;
                try
                {
                    length = new FileInfo(file).Length;
                }
                catch (Exception ex)
                {
                    _diagnostics.Report(new LoadError(
                        DiagnosticCodes.CacheIndexFailed, LoadStage.CacheLookup, CleanupStatus.Complete, ex,
                        new Dictionary<string, object> { { "file", fileName } }));
                    continue;
                }
                completeFiles.Add((file, parsed.Value.Key, parsed.Value.CompletedAtUtc, length));
                // 元组元素名统一为 CompletedAt
            }

            // 2. 同 key 只保留最新完整文件，尽力删除旧副本 (清理只发生于这个启动阶段)
            var survivors = completeFiles
                .GroupBy(f => f.Key, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(f => f.CompletedAtUtc).First())
                .ToList();
            foreach (var stale in completeFiles.Where(f => !survivors.Contains(f)))
            {
                _TryDeleteFile(stale.Path, "startup-stale-copy");
            }

            // 3. 超启动目标：按完成时间从旧到新尝试删除；删除成功才减少本次扫描统计
            if (_options.StartupTargetBytes is long target)
            {
                var total = survivors.Sum(f => f.Length);
                foreach (var candidate in survivors.OrderBy(f => f.CompletedAtUtc).ToList())
                {
                    if (total <= target)
                    {
                        break;
                    }
                    if (_TryDeleteFile(candidate.Path, "startup-target"))
                    {
                        total -= candidate.Length;
                        survivors.Remove(candidate);
                    }
                }
            }

            // 4. 从实际仍存在的文件建立内存字典
            lock (_gate)
            {
                foreach (var survivor in survivors)
                {
                    _records[survivor.Key] = new CachedRecord(
                        survivor.Key, survivor.Path, survivor.CompletedAtUtc, survivor.Length);
                }
            }
            return Task.CompletedTask;
        }

        private static (string Key, DateTime CompletedAtUtc)? _ParseCacheFileName(string fileName)
        {
            // <keyHash>_<completedUtcTicks>_<randomId>.cache
            var stem = fileName.Substring(0, fileName.Length - CacheFileSuffix.Length);
            var parts = stem.Split('_');
            if (parts.Length != 3 || parts[0].Length != 64 || !long.TryParse(parts[1], out var ticks) || ticks <= 0)
            {
                return null;
            }
            return (parts[0], new DateTime(ticks, DateTimeKind.Utc));
        }

        private bool _TryDeleteFile(string path, string reason)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return !File.Exists(path);
            }
            catch (Exception ex)
            {
                _diagnostics.Report(new LoadError(
                    DiagnosticCodes.FileIoFailed, LoadStage.CacheEvict, CleanupStatus.Complete, ex,
                    new Dictionary<string, object> { { "file", Path.GetFileName(path) }, { "reason", reason } }));
                return false;
            }
        }

        private static string[] _ListFilesSafe(string directory)
        {
            try
            {
                return Directory.Exists(directory) ? Directory.GetFiles(directory) : Array.Empty<string>();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        #endregion

        #region 取得文件

        /// <summary>
        /// 返回已完成文件的绝对路径；调用者不释放、不删除该文件。
        /// 有效本地命中不发网络请求；无有效文件时加入同 key 共享下载或启动一次下载。
        /// </summary>
        public async Task<string> GetFileAsync(FileRequest request, CancellationToken cancellationToken = default)
        {
            _ValidateRequest(request);
            var key = FileKeyEncoding.Encode(request.Identity);

            if (Volatile.Read(ref _initializeStarted) == 0)
            {
                throw new InvalidOperationException(
                    "FileCache 尚未初始化：必须在任何资源开始加载前完成 InitializeAsync");
            }
            await _initializeTask.WaitWithCancellation(cancellationToken).ConfigureAwait(false); // 取消只中断等待

            string? hitPath = null;
            Task<string>? shared = null;
            lock (_gate)
            {
                if (_state != FileCacheState.Open)
                {
                    throw _ClosedError();
                }
                if (_records.TryGetValue(key, out var record) && _RecordStillValid(request, record))
                {
                    hitPath = record.Path;
                }

                if (hitPath == null && _downloads.TryGetValue(key, out var existing))
                {
                    _AssertJoinCompatible(request, existing);
                    shared = existing;
                }
                else
                {
                    _AssertValidityCompatible(request);
                    // 先登记共享任务与发起请求声明，再启动底层操作，防止同步完成时漏记
                    var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var task = completion.Task;
                    _activeRequestClaims[task] = (request.ExpectedLength, request.ExpectedSha256);
                    _downloads[key] = task;
                    _ = _DownloadAndPublishAsync(request, key, completion);
                    shared = task;
                }
            }

            if (hitPath != null)
            {
                cancellationToken.ThrowIfCancellationRequested(); // 交付边界检查调用者令牌
                await _VerifyDeliveryAsync(request, key, hitPath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return hitPath;
            }

            await shared!.WaitWithCancellation(cancellationToken).ConfigureAwait(false); // 只取消自己的等待
            var path = shared.Result; // 等待已成功结束，读取共享任务结果
            await _VerifyDeliveryAsync(request, key, path, cancellationToken).ConfigureAwait(false);
            return path;
        }

        private static void _ValidateRequest(FileRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!request.Source.IsAbsoluteUri)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.NetworkInvalidUri, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "source", request.Source.ToString() } }));
            }
            if (request.ExpectedLength is long len && len < 0)
            {
                throw new ArgumentException("ExpectedLength 不能为负");
            }
            if (request.ExpectedSha256 != null && request.ExpectedSha256.Length != 64)
            {
                throw new ArgumentException("ExpectedSha256 必须是 64 位十六进制摘要");
            }
        }

        /// <summary> 内存记录有效性：完整文件存在 + TTL 未到期 (命中不延长 TTL)；过期旧文件留下次启动处理 </summary>
        private bool _RecordStillValid(FileRequest request, CachedRecord record)
        {
            if (request.Validity.Mode == ValidityMode.ExpiresAfter
                && _utcNow() > record.CompletedAtUtc + request.Validity.Ttl)
            {
                return false;
            }
            return File.Exists(record.Path);
        }

        private void _AssertJoinCompatible(FileRequest incoming, Task<string> sharedTask)
        {
            if (_activeRequestClaims.TryGetValue(sharedTask, out var claims))
            {
                if (incoming.ExpectedLength != null && claims.Length != null
                    && incoming.ExpectedLength != claims.Length
                    || incoming.ExpectedSha256 != null && claims.Sha256 != null
                    && !string.Equals(incoming.ExpectedSha256, claims.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ResourceLoadException(_IdentityConflict(incoming));
                }
            }
            _AssertValidityCompatible(incoming);
        }

        private void _AssertValidityCompatible(FileRequest request)
        {
            if (_establishedValidities.TryGetValue(request.Identity, out var established)
                && !established.IsCompatibleWith(request.Validity))
            {
                throw new ResourceLoadException(_IdentityConflict(request));
            }
        }

        private static LoadError _IdentityConflict(FileRequest request)
        {
            return new LoadError(
                DiagnosticCodes.CacheIdentityConflict, LoadStage.CacheLookup, CleanupStatus.Complete, null,
                new Dictionary<string, object>
                {
                    { "identity", request.Identity.ToString() },
                    { "url", request.Source.Scheme + "://" + request.Source.Host + request.Source.AbsolutePath },
                });
        }

        /// <summary>
        /// 交付核验：命中路径和共享任务返回的路径都必须满足本次调用者声明；
        /// 首个请求未声明摘要不能让后来请求的摘要要求丢失。需要 I/O 的摘要计算在临界区外进行。
        /// </summary>
        private async Task _VerifyDeliveryAsync(FileRequest request, string key, string path, CancellationToken ct)
        {
            long actualLength;
            try
            {
                actualLength = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.FileNotFound, LoadStage.CacheLookup, CleanupStatus.Complete, ex,
                    new Dictionary<string, object> { { "key", key } }));
            }
            if (request.ExpectedLength is long expectedLength && actualLength != expectedLength)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.CacheIntegrityFailed, LoadStage.ValidateContent, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "key", key },
                        { "expected", expectedLength },
                        { "actual", actualLength },
                    }));
            }
            if (request.ExpectedSha256 != null)
            {
                string actualSha;
                try
                {
                    actualSha = await _ComputeSha256Async(path, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.FileIoFailed, LoadStage.ReadFile, CleanupStatus.Complete, ex,
                        new Dictionary<string, object> { { "key", key } }));
                }
                if (!string.Equals(request.ExpectedSha256, actualSha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.CacheIntegrityFailed, LoadStage.ValidateContent, CleanupStatus.Complete, null,
                        new Dictionary<string, object> { { "key", key }, { "reason", "sha256-mismatch" } }));
                }
            }
        }

        private static async Task<string> _ComputeSha256Async(string path, CancellationToken ct)
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, useAsync: true);
            var hash = sha.ComputeHash(stream);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }

        #endregion

        #region 下载并发布

        /// <summary>
        /// 共享下载任务：使用缓存自身生命周期令牌 (不是任何调用者的取消令牌)；
        /// 每次尝试独立持有响应、流与临时 .part；改名发布是路径可见性的分界，
        /// 发布后的完整文件不回删。所有路径都完成共享任务的成功/失败信号。
        /// </summary>
        private async Task _DownloadAndPublishAsync(
            FileRequest request, string key, TaskCompletionSource<string> completion)
        {
            var task = completion.Task;
            lock (_gate)
            {
                if (!_establishedValidities.ContainsKey(request.Identity))
                {
                    _establishedValidities[request.Identity] = request.Validity;
                }
            }

            var cacheToken = _lifetimeCts.Token;
            var slotAcquired = false;
            try
            {
                await _downloadSlots.WaitAsync(cacheToken).ConfigureAwait(false);
                slotAcquired = true;
                var path = await _DownloadWithRetriesAsync(request, key, cacheToken).ConfigureAwait(false);
                completion.TrySetResult(path);
            }
            catch (Exception ex)
            {
                // 共享任务的异常即使无人等待也要被观察；最终故障按诊断规则报告，
                // 不向已取消的业务调用者发送过期结果 (其等待已以 OCE 结束)
                completion.TrySetException(ex);
                var error = ex is ResourceLoadException rle
                    ? rle.Error
                    : new LoadError(DiagnosticCodes.InternalUnexpected, LoadStage.Download,
                        CleanupStatus.Complete, ex, new Dictionary<string, object> { { "key", key } });
                _diagnostics.Report(error);
            }
            finally
            {
                if (slotAcquired)
                {
                    _downloadSlots.Release();
                }
                lock (_gate)
                {
                    _activeRequestClaims.Remove(task);
                    // 按实际任务身份移除共享下载记录
                    if (_downloads.TryGetValue(key, out var current) && ReferenceEquals(current, task))
                    {
                        _downloads.Remove(key);
                    }
                }
            }
        }

        private async Task<string> _DownloadWithRetriesAsync(
            FileRequest request, string key, CancellationToken cacheToken)
        {
            var maxAttempts = 1 + _network.MaxRetries;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                LoadError failure;
                var partPath = Path.Combine(_CacheDirectory(), Guid.NewGuid().ToString("N") + PartFileSuffix);
                try
                {
                    var (path, error) = await _DownloadOnceAsync(request, key, partPath, cacheToken)
                        .ConfigureAwait(false);
                    if (error == null)
                    {
                        return path!; // finally 删除已不存在的 part (改名成功)
                    }
                    failure = error;
                }
                catch (OperationCanceledException) when (cacheToken.IsCancellationRequested)
                {
                    throw; // 缓存关闭：由共享任务收尾统一处理
                }
                catch (Exception ex)
                {
                    failure = _ClassifyDownloadFailure(ex, key);
                }
                finally
                {
                    // 每次尝试独立清理自身 .part；发布成功后 part 已不存在
                    _TryDeleteFile(partPath, "attempt-cleanup");
                }

                if (_IsTransient(failure) && attempt < maxAttempts)
                {
                    await Task.Delay(_BackoffDelay(attempt), cacheToken).ConfigureAwait(false);
                    continue;
                }
                throw new ResourceLoadException(failure);
            }
            throw new InvalidOperationException("下载重试循环不可达");
        }

        private async Task<(string? Path, LoadError? Error)> _DownloadOnceAsync(
            FileRequest request, string key, string partPath, CancellationToken cacheToken)
        {
            TransportResponse? response = null;
            FileStream? writeStream = null;
            try
            {
                response = await _transport.OpenReadAsync(request, cacheToken).ConfigureAwait(false);
                writeStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    81920, useAsync: true);
                var buffer = new byte[81920];
                long written = 0;
                string hashHex;
                using (var sha = SHA256.Create())
                {
                    while (true)
                    {
                        cacheToken.ThrowIfCancellationRequested();
                        var read = await response.Body.ReadAsync(buffer, 0, buffer.Length, cacheToken)
                            .ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }
                        if (request.ExpectedLength is long expected && written + read > expected)
                        {
                            return (null, new LoadError(
                                DiagnosticCodes.CacheIntegrityFailed, LoadStage.ValidateContent,
                                CleanupStatus.Complete, null,
                                _FactsOf(key, request, "written-exceeds-expected")));
                        }
                        await writeStream.WriteAsync(buffer, 0, read, cacheToken).ConfigureAwait(false);
                        sha.TransformBlock(buffer, 0, read, buffer, 0);
                        written += read;
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    hashHex = _ToHex(sha.Hash!);
                }

                await writeStream.FlushAsync(cacheToken).ConfigureAwait(false);
                writeStream.Dispose();
                writeStream = null;
                await response.DisposeAsync().ConfigureAwait(false);
                response = null;

                if (response != null) { /* 不可达：仅为消除可空警告的结构占位 */ }

                // 发布前校验：长度与发起请求声明的摘要
                if (request.ExpectedLength is long expect && written != expect)
                {
                    return (null, new LoadError(
                        DiagnosticCodes.CacheIntegrityFailed, LoadStage.ValidateContent,
                        CleanupStatus.Complete, null, _FactsOf(key, request, $"written={written}, expected={expect}")));
                }
                if (request.ExpectedSha256 != null
                    && !string.Equals(request.ExpectedSha256, hashHex, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, new LoadError(
                        DiagnosticCodes.CacheIntegrityFailed, LoadStage.ValidateContent,
                        CleanupStatus.Complete, null, _FactsOf(key, request, "sha256-mismatch")));
                }
                if (cacheToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cacheToken);
                }

                // 改名发布：唯一完整路径 = <key>_<completedTicks>_<randomId>.cache，不覆盖已有路径
                var completedAt = _utcNow();
                var finalPath = Path.Combine(_CacheDirectory(),
                    $"{key}_{completedAt.Ticks:0}_{Guid.NewGuid().ToString("N")}{CacheFileSuffix}");
                File.Move(partPath, finalPath);

                lock (_gate)
                {
                    if (_state == FileCacheState.Open)
                    {
                        _records[key] = new CachedRecord(key, finalPath, completedAt, written);
                    }
                }
                return (finalPath, null);
            }
            finally
            {
                if (writeStream != null)
                {
                    try { writeStream.Dispose(); } catch { /* 关闭失败由 part 清理兜底 */ }
                }
                if (response != null)
                {
                    try { await response.DisposeAsync().ConfigureAwait(false); }
                    catch { /* 尽力关闭 */ }
                }
            }
        }

        private static bool _IsTransient(LoadError error)
        {
            switch (error.DiagnosticCode)
            {
                case DiagnosticCodes.NetworkTimeout:
                case DiagnosticCodes.NetworkConnectFailed:
                case DiagnosticCodes.NetworkInterrupted:
                case DiagnosticCodes.NetworkHttpThrottled:
                case DiagnosticCodes.NetworkHttpServerError:
                    return true;
                default:
                    return false; // 地址错误、404、权限、磁盘满、校验失败不自动重试
            }
        }

        private TimeSpan _BackoffDelay(int attempt)
        {
            return TimeSpan.FromSeconds(_network.RetryBaseDelay.TotalSeconds * Math.Pow(2, attempt - 1));
        }

        private static LoadError _ClassifyDownloadFailure(Exception ex, string key)
        {
            string code;
            if (FileSystemErrorClassifier.IsDiskFull(ex))
            {
                code = DiagnosticCodes.CacheDiskFull;
            }
            else if (FileSystemErrorClassifier.IsAccessDenied(ex))
            {
                code = DiagnosticCodes.FileAccessDenied;
            }
            else if (ex is IOException)
            {
                code = DiagnosticCodes.NetworkInterrupted;
            }
            else if (ex is OperationCanceledException)
            {
                code = DiagnosticCodes.NetworkTimeout;
            }
            else
            {
                code = DiagnosticCodes.InternalUnexpected;
            }
            return new LoadError(code, LoadStage.Download, CleanupStatus.Complete, ex,
                new Dictionary<string, object> { { "key", key } });
        }

        private static Dictionary<string, object> _FactsOf(string key, FileRequest request, string fact)
        {
            return new Dictionary<string, object>
            {
                { "key", key },
                { "url", request.Source.Scheme + "://" + request.Source.Host + request.Source.AbsolutePath },
                { "fact", fact },
            };
        }

        private static string _ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }

        #endregion

        #region 关闭

        /// <summary>
        /// 唯一独立执行的关闭任务；每次调用等待同一过程，调用者令牌只取消自己的等待。
        /// 停止接收新 Get，取消并等待在途下载收尾；不删除完整文件、不等待文件使用者、
        /// 不释放注入的 transport (装配根在缓存关闭完成后释放)。
        /// </summary>
        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
            {
                _shutdownTask = _ShutdownCoreAsync();
            }
            return _shutdownTask.WaitWithCancellation(cancellationToken);
        }

        private async Task _ShutdownCoreAsync()
        {
            List<Task<string>> inFlight;
            lock (_gate)
            {
                if (_state == FileCacheState.NotInitialized || _state == FileCacheState.Closed)
                {
                    _state = FileCacheState.Closed; // 从未初始化的关闭：此后不再接受 Initialize
                    _lifetimeCts.Dispose();
                    _downloadSlots.Dispose();
                    return;
                }
                _state = FileCacheState.Closing;
                _lifetimeCts.Cancel(); // 停止初始化与在途/排队下载
                inFlight = _downloads.Values.ToList();
            }

            try
            {
                if (inFlight.Count > 0)
                {
                    // 等真实任务收尾，不因某个等待者先结束就提前完成
                    await Task.WhenAll(inFlight.Select(t => t.ContinueWith(_ => (object?)null)))
                        .ConfigureAwait(false);
                }
                await _initializeTask.ConfigureAwait(false); // 初始化进行中：等待其收尾
            }
            catch (Exception)
            {
                // 关闭路径中初始化/下载以取消或失败结束是预期结果
            }

            lock (_gate)
            {
                _state = FileCacheState.Closed;
            }
            _lifetimeCts.Dispose();
            _downloadSlots.Dispose();
        }

        private static ResourceLoadException _ClosedError()
        {
            return new ResourceLoadException(new LoadError(
                DiagnosticCodes.LifecycleSystemClosing, LoadStage.CacheLookup, CleanupStatus.Complete));
        }

        #endregion
    }
}
