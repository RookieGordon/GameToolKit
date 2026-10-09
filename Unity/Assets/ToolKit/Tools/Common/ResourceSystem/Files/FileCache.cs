/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 纯文件缓存 (HTTP 解耦版)。本地有有效文件就复用，没有就调用 fill 回调写入临时路径。
 *                缓存只负责：身份/TTL/校验、本地命中、同 key 共享一次填充、临时路径分配、
 *                发布唯一完整文件、启动一次清理。不做下载、不做重试、不解析网络错误——
 *                网络下载只是 fill 的一种实现，全部网络行为归下载模块。
 *                fill 使用缓存生命周期令牌；调用者取消只结束自己的等待，
 *                即使所有等待者取消，填充也允许完成并进入缓存。
 *                文件命名：<keyHash>_<completedUtcTicks>_<randomId>.cache (目录名 http-cache-v2 为历史兼容)。
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
        private const string VersionDirectoryName = "http-cache-v2"; // 历史兼容目录名
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
        private readonly IResourceDiagnostics _diagnostics;
        private readonly Func<DateTime> _utcNow;

        private readonly object _gate = new object();        // 短同步临界区：记录表/共享任务表/状态
        private readonly Dictionary<string, CachedRecord> _records =
            new Dictionary<string, CachedRecord>(StringComparer.Ordinal);

        private readonly Dictionary<string, Task<string>> _fills =
            new Dictionary<string, Task<string>>(StringComparer.Ordinal);

        /// <summary> 共享填充发起请求的声明 (长度/摘要)，用于加入时的身份冲突检查 </summary>
        private readonly Dictionary<Task<string>, (long? Length, string? Sha256)> _activeFillClaims =
            new Dictionary<Task<string>, (long?, string?)>();

        private readonly Dictionary<FileIdentity, FileValidity> _establishedValidities =
            new Dictionary<FileIdentity, FileValidity>();

        /// <summary> 填充方法任务本体 (含 finally 收尾)：关闭等待它们，保证真实操作结束后才收尾 </summary>
        private readonly List<Task> _runningFills = new List<Task>();
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();

        private FileCacheState _state = FileCacheState.NotInitialized;
        private Task _initializeTask = Task.CompletedTask;
        private Task _shutdownTask = Task.CompletedTask;
        private int _initializeStarted;
        private int _shutdownStarted;

        public FileCache(FileCacheOptions options,
            IResourceDiagnostics? diagnostics = null,
            Func<DateTime>? utcNow = null)
        {
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Clone();
            _options.Validate();
            _diagnostics = new SafeResourceDiagnostics(diagnostics ?? NullResourceDiagnostics.Instance);
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        #region 初始化 (启动清理，只执行一次)

        /// <summary>
        /// 启动次序：创建专用版本子目录 → 枚举受管文件 → 尽力删 .part → 同 key 留最新 →
        /// 超启动目标按完成时间从旧到新删除 → 建立内存字典。不创建 HTTP 客户端、不连接服务器。
        /// 删除失败记录诊断并继续；取消等待不撤销已开始的初始化；重复调用等待同一任务。
        /// </summary>
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _initializeStarted, 1) == 0)
            {
                _initializeTask = _InitializeCoreAsync();
            }
            else if (Volatile.Read(ref _shutdownStarted) == 1)
            {
                throw new InvalidOperationException("缓存已关闭，不再接受 InitializeAsync");
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
                _StartupCleanup(directory);
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
        private void _StartupCleanup(string directory)
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

        #region 取得或填充

        /// <summary>
        /// 返回已完成文件的绝对路径：有效本地命中直接返回 (完全不调用 fill)；
        /// 未命中时同 key 共享一次填充——fill 向缓存分配的临时路径写入完整内容后返回，
        /// 缓存执行长度/摘要校验并发布唯一完整文件。调用者不释放、不删除该文件。
        /// fill 失败或取消时必须先结束自身 I/O 再把异常交回；缓存清理自己的临时文件。
        /// </summary>
        public async Task<string> GetOrCreateAsync(
            FileRequest request,
            Func<string, CancellationToken, Task> fill,
            CancellationToken cancellationToken = default)
        {
            if (fill == null) throw new ArgumentNullException(nameof(fill));
            _ValidateRequest(request);
            var key = FileKeyEncoding.Encode(request.Identity);

            if (Volatile.Read(ref _initializeStarted) == 0)
            {
                throw new InvalidOperationException(
                    "FileCache 尚未初始化：必须在任何资源开始加载前完成 InitializeAsync");
            }
            await _initializeTask.WaitWithCancellation(cancellationToken).ConfigureAwait(false); // 取消只中断等待

            string? hitPath = null;
            Task<string>? shared;
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

                if (hitPath == null)
                {
                    if (_fills.TryGetValue(key, out var existing))
                    {
                        _AssertJoinCompatible(request, existing);
                        shared = existing;
                    }
                    else
                    {
                        _AssertValidityCompatible(request);
                        // 先登记共享任务与发起请求声明，再启动填充，防止同步完成时漏记
                        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var task = completion.Task;
                        _activeFillClaims[task] = (request.ExpectedLength, request.ExpectedSha256);
                        _fills[key] = task;
                        var methodTask = _FillAndPublishAsync(request, key, fill, completion);
                        _runningFills.Add(methodTask);
                        _ = methodTask.ContinueWith(t =>
                        {
                            lock (_gate)
                            {
                                _runningFills.Remove(methodTask);
                            }
                        }, TaskScheduler.Default);
                        shared = task;
                    }
                }
                else
                {
                    shared = null!;
                }
            }

            if (hitPath != null)
            {
                cancellationToken.ThrowIfCancellationRequested(); // 交付边界检查调用者令牌
                try
                {
                    await _VerifyDeliveryAsync(request, key, hitPath, cancellationToken).ConfigureAwait(false);
                }
                catch (ResourceLoadException ex) when (
                    ex.Error.DiagnosticCode == DiagnosticCodes.CacheIntegrityFailed)
                {
                    // 校验不符：停止复用该内存记录 (旧完整文件留下次启动处理)
                    lock (_gate)
                    {
                        if (_records.TryGetValue(key, out var stale) && stale.Path == hitPath)
                        {
                            _records.Remove(key);
                        }
                    }
                    throw;
                }
                cancellationToken.ThrowIfCancellationRequested();
                return hitPath;
            }

            await shared.WaitWithCancellation(cancellationToken).ConfigureAwait(false); // 只取消自己的等待
            var path = shared.Result;
            await _VerifyDeliveryAsync(request, key, path, cancellationToken).ConfigureAwait(false);
            return path;
        }

        private static void _ValidateRequest(FileRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
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
            if (_activeFillClaims.TryGetValue(sharedTask, out var claims))
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
                new Dictionary<string, object> { { "identity", request.Identity.ToString() } });
        }

        /// <summary>
        /// 交付核验：命中路径和共享填充返回的路径都必须满足本次调用者声明；
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

        #endregion

        #region 填充并发布

        /// <summary>
        /// 共享填充任务：分配独占临时路径 → 在临界区外调用 fill → 校验 → 改名发布唯一完整文件。
        /// fill 使用缓存生命周期令牌 (不是任何调用者的取消令牌)；一次共享填充只调用一次 fill。
        /// 发布后的完整文件不回删；所有路径都完成共享任务的结果信号。
        /// </summary>
        private async Task _FillAndPublishAsync(
            FileRequest request, string key, Func<string, CancellationToken, Task> fill,
            TaskCompletionSource<string> completion)
        {
            var task = completion.Task;
            var partPath = Path.Combine(_CacheDirectory(), Guid.NewGuid().ToString("N") + PartFileSuffix);
            try
            {
                Directory.CreateDirectory(_CacheDirectory());
                lock (_gate)
                {
                    if (!_establishedValidities.ContainsKey(request.Identity))
                    {
                        _establishedValidities[request.Identity] = request.Validity;
                    }
                }

                await fill(partPath, _lifetimeCts.Token).ConfigureAwait(false); // 网络重试全部在 fill 内部

                // fill 成功返回后不再持有写入句柄；校验并发布
                long actualLength;
                string actualSha;
                try
                {
                    actualLength = new FileInfo(partPath).Length;
                    actualSha = await _ComputeSha256Async(partPath, _lifetimeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.FileIoFailed, LoadStage.ValidateContent, CleanupStatus.Complete, ex,
                        new Dictionary<string, object> { { "key", key }, { "reason", "verify-read-failed" } }));
                }
                if (request.ExpectedLength is long expected && actualLength != expected)
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.CacheIntegrityFailed, LoadStage.ValidateContent, CleanupStatus.Complete, null,
                        new Dictionary<string, object>
                        {
                            { "key", key },
                            { "expected", expected },
                            { "actual", actualLength },
                        }));
                }
                if (request.ExpectedSha256 != null
                    && !string.Equals(request.ExpectedSha256, actualSha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.CacheIntegrityFailed, LoadStage.ValidateContent, CleanupStatus.Complete, null,
                        new Dictionary<string, object> { { "key", key }, { "reason", "sha256-mismatch" } }));
                }

                _lifetimeCts.Token.ThrowIfCancellationRequested();

                // 改名发布：唯一完整路径 = <key>_<completedTicks>_<randomId>.cache，不覆盖已有路径
                var completedAt = _utcNow();
                var finalPath = Path.Combine(_CacheDirectory(),
                    $"{key}_{completedAt.Ticks:0}_{Guid.NewGuid().ToString("N")}{CacheFileSuffix}");
                File.Move(partPath, finalPath);

                lock (_gate)
                {
                    if (_state == FileCacheState.Open)
                    {
                        _records[key] = new CachedRecord(key, finalPath, completedAt, actualLength);
                    }
                }
                completion.TrySetResult(finalPath);
            }
            catch (Exception ex)
            {
                // 共享任务的异常即使无人等待也要被观察；缓存对已分类错误透传，不猜测网络
                completion.TrySetException(ex);
                if (ex is not OperationCanceledException)
                {
                    _diagnostics.Report(ex is ResourceLoadException rle ? rle.Error : new LoadError(
                        DiagnosticCodes.InternalUnexpected, LoadStage.WaitForLoad,
                        CleanupStatus.Complete, ex, new Dictionary<string, object> { { "key", key } }));
                }
            }
            finally
            {
                _TryDeleteFile(partPath, "fill-cleanup"); // 发布成功后 part 已不存在；失败清理自身临时文件
                lock (_gate)
                {
                    _activeFillClaims.Remove(task);
                    if (_fills.TryGetValue(key, out var current) && ReferenceEquals(current, task))
                    {
                        _fills.Remove(key);
                    }
                }
            }
        }

        private static async Task<string> _ComputeSha256Async(string path, CancellationToken ct)
        {
            using var sha = SHA256.Create();
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, useAsync: true);
            var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }

        #endregion

        #region 关闭

        /// <summary>
        /// 唯一独立执行的关闭任务；每次调用等待同一过程，调用者令牌只取消自己的等待。
        /// 停止接收新请求，经缓存生命周期令牌取消共享填充并等待实际操作收尾；
        /// 不删除完整文件、不等待文件使用者。
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
            List<Task> inFlight;
            lock (_gate)
            {
                if (_state == FileCacheState.NotInitialized || _state == FileCacheState.Closed)
                {
                    _state = FileCacheState.Closed; // 从未初始化的关闭：此后不再接受 Initialize
                    _lifetimeCts.Dispose();
                    return;
                }
                _state = FileCacheState.Closing;
                _lifetimeCts.Cancel(); // 取消进行中的共享填充 (fill 观察缓存生命周期令牌)
                inFlight = _runningFills.ToList();
            }

            try
            {
                if (inFlight.Count > 0)
                {
                    // 等待填充方法任务本体收尾 (含 finally 的临时文件清理)
                    await Task.WhenAll(inFlight).ConfigureAwait(false);
                }
                await _initializeTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 填充/初始化以取消或失败结束是关闭路径的预期结果
            }

            lock (_gate)
            {
                _state = FileCacheState.Closed;
            }
            _lifetimeCts.Dispose();
        }

        private static ResourceLoadException _ClosedError()
        {
            return new ResourceLoadException(new LoadError(
                DiagnosticCodes.LifecycleSystemClosing, LoadStage.CacheLookup, CleanupStatus.Complete));
        }

        #endregion
    }
}
