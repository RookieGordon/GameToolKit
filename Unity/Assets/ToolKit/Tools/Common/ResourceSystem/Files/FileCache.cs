/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 磁盘缓存 (P4, §10)。远端文件默认先查本地缓存，未命中才下载，受可核算的
 *                磁盘容量预算约束。所有身份、条目状态、等待者、pin、A/R 更新在同一个串行
 *                状态调度器 (lock _state) 内执行；网络与文件 I/O 在外执行，完成后回到 S 提交。
 *                容量不变量：每次写入授权满足 A + R <= M；写入前已有至少 n 字节预留；
 *                写入后 A += n、R -= n；删除仅在文件确认不存在后扣减 A。
 *                提交顺序：数据文件完成 → 元数据原子重命名 (提交标记)。第一版不恢复 .part。
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
    /// <summary> 缓存条目：一个身份一个代次的实际文件与持久化元数据 </summary>
    internal sealed class FileEntry
    {
        public readonly string Key;
        public readonly FileIdentity Identity;
        public readonly long Generation;
        public FileEntryState State;
        public readonly string DataPath;
        public readonly string MetaPath;
        public long Length;
        public long MetaLength;
        public readonly string Digest;
        public readonly DateTime StoredAtUtc;
        public readonly DateTime ValidUntilUtc;
        public readonly bool ValidityExpires;
        public readonly TimeSpan Ttl;
        public readonly long? LengthClaim;
        public readonly string? ShaClaim;
        public DateTime LastAccessUtc;
        public bool DirtyAccess;
        public int PinCount;
        public LoadError? DeleteError;
        public double NextDeleteRetryAt;

        public FileEntry(string key, FileIdentity identity, long generation, string dataPath, string metaPath,
            long length, long metaLength, string digest, DateTime storedAtUtc, DateTime validUntilUtc,
            bool validityExpires, TimeSpan ttl, long? lengthClaim, string? shaClaim)
        {
            Key = key;
            Identity = identity;
            Generation = generation;
            DataPath = dataPath;
            MetaPath = metaPath;
            Length = length;
            MetaLength = metaLength;
            Digest = digest;
            StoredAtUtc = storedAtUtc;
            ValidUntilUtc = validUntilUtc;
            ValidityExpires = validityExpires;
            Ttl = ttl;
            LengthClaim = lengthClaim;
            ShaClaim = shaClaim;
        }

        public bool IsExpired(DateTime utcNow)
        {
            return ValidityExpires && utcNow > ValidUntilUtc;
        }

        /// <summary> 受管路径与已计量长度 (数据 + 元数据) </summary>
        public List<KeyValuePair<string, long>> OwnedFiles()
        {
            var list = new List<KeyValuePair<string, long>>(2)
            {
                new KeyValuePair<string, long>(DataPath, Length),
            };
            if (MetaPath.Length > 0 && MetaLength > 0)
            {
                list.Add(new KeyValuePair<string, long>(MetaPath, MetaLength));
            }
            return list;
        }
    }

    /// <summary> 合并下载作业；等待者独立状态与取消 </summary>
    internal sealed class DownloadJob
    {
        public readonly long Id;
        public readonly string Key;
        public readonly long Generation;
        public readonly FileRequest Request;
        public readonly string OperationId;
        public DownloadJobState State;
        public readonly List<FileWaiter> Waiters = new List<FileWaiter>();
        public string? PartPath;
        public string? MetaTmpPath;
        public long WrittenBytes;
        public long ReservedBytes;
        public int Attempt;
        public readonly CancellationTokenSource CancellationSource = new CancellationTokenSource();
        public readonly TaskCompletionSource<object> Terminal =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        public DownloadJob(long id, string key, long generation, FileRequest request, string operationId)
        {
            Id = id;
            Key = key;
            Generation = generation;
            Request = request;
            OperationId = operationId;
            State = DownloadJobState.Queued;
        }

        public bool IsAccepting =>
            State == DownloadJobState.Queued || State == DownloadJobState.Opening
            || State == DownloadJobState.Downloading || State == DownloadJobState.Verifying
            || State == DownloadJobState.Committing;
    }

    /// <summary> 获取等待者：一个等待者最多获得一个结果；完成与取消由同一状态调度器裁定 </summary>
    internal sealed class FileWaiter
    {
        public readonly long Id;
        public WaiterState State;
        public readonly TaskCompletionSource<object> Completion;
        public readonly CancellationToken CallerToken;
        public CancellationTokenRegistration Registration;
        public DownloadJob? Job;

        public FileWaiter(long id, CancellationToken callerToken)
        {
            Id = id;
            CallerToken = callerToken;
            Completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public sealed class FileCache
    {
        private const int MetaMagic = unchecked((int)0x47544632); // "GTF2"
        private const int MetaSchemaVersion = 1;
        private static readonly TimeSpan AccessFlushInterval = TimeSpan.FromSeconds(60);

        private readonly FileCacheOptions _options;
        private readonly NetworkOptions _network;
        private readonly IFileTransport? _transport;
        private readonly IFileCacheFileSystem _fs;
        private readonly IResourceDiagnostics _diagnostics;
        private readonly Func<double> _monotonicNow;
        private readonly Func<DateTime> _utcNow;

        private readonly object _state = new object();
        private readonly Dictionary<string, FileEntry> _ready = new Dictionary<string, FileEntry>();
        private readonly Dictionary<string, DownloadJob> _jobs = new Dictionary<string, DownloadJob>();
        private readonly List<FileEntry> _tracked = new List<FileEntry>();
        private readonly SemaphoreSlim _downloadSlots;
        private readonly TaskCompletionSource<object> _shutdownTcs =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        private FileCacheState _cacheState = FileCacheState.Initializing;
        private Task _shutdownCoreTask = Task.CompletedTask;
        private long _accountedBytes;
        private long _reservedBytes;
        private int _activeDeletes;
        private bool _accessFlushInFlight;
        private DateTime _lastAccessFlushUtc;
        private FileStream? _lockHandle;
        private long _waiterSeed;
        private long _generationSeed;
        private long _jobSeed;
        private int _shutdownStarted;

        private static readonly object RandomGate = new object();
        private static readonly Random RandomSource = new Random();

        public FileCache(
            FileCacheOptions options,
            NetworkOptions? network = null,
            IFileTransport? transport = null,
            IFileCacheFileSystem? fileSystem = null,
            Func<double>? monotonicNow = null,
            Func<DateTime>? utcNow = null,
            IResourceDiagnostics? diagnostics = null)
        {
            options ??= new FileCacheOptions();
            options.Validate();
            _options = new FileCacheOptions
            {
                Directory = options.Directory,
                MaxBytes = options.MaxBytes,
                TrimToRatio = options.TrimToRatio,
                ChunkBytes = options.ChunkBytes,
                MetadataAllowanceBytes = options.MetadataAllowanceBytes,
                ResolvePath = options.ResolvePath,
            };
            network ??= new NetworkOptions();
            network.Validate();
            _network = new NetworkOptions
            {
                MaxConcurrentDownloads = network.MaxConcurrentDownloads,
                ConnectTimeout = network.ConnectTimeout,
                ResponseTimeout = network.ResponseTimeout,
                MaxRetries = network.MaxRetries,
                RetryBaseDelay = network.RetryBaseDelay,
            };
            _transport = transport;
            _fs = fileSystem ?? PhysicalFileCacheFileSystem.Instance;
            _diagnostics = new SafeResourceDiagnostics(diagnostics ?? NullResourceDiagnostics.Instance);
            _monotonicNow = monotonicNow ?? _DefaultMonotonicNow;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _downloadSlots = new SemaphoreSlim(Math.Max(1, _network.MaxConcurrentDownloads),
                Math.Max(1, _network.MaxConcurrentDownloads));
        }

        private static double _DefaultMonotonicNow()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        }

        #region 初始化与启动恢复 (§10.7)

        /// <summary>
        /// 启动次序：取得目录独占锁 → 枚举受管文件并统计占用 → 校验元数据、建立索引 →
        /// 清理孤立文件 → 容量回收 → 对外接受请求。必须在使用前调用一次。
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            lock (_state)
            {
                if (_cacheState != FileCacheState.Initializing)
                {
                    throw new InvalidOperationException($"FileCache 已初始化 (state={_cacheState})");
                }
            }

            _fs.CreateDirectory(_options.Directory);
            _fs.CreateDirectory(_StagingDir());
            var lockPath = Path.Combine(_options.Directory, "gt-cache.lock");

            try
            {
                // 根目录独占锁：单进程单实例；第二实例无法取得锁时启动失败
                _lockHandle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex)
            {
                _cacheState = FileCacheState.Faulted;
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.CacheRootInUse, LoadStage.CacheLookup, CleanupStatus.Complete, ex,
                    new Dictionary<string, object> { { "directory", _options.Directory } }));
            }

            await _RecoverIndexAsync(cancellationToken).ConfigureAwait(false);

            lock (_state)
            {
                // 锁文件等固定管理开销启动时计入
                _accountedBytes += _lockHandle.Length;
                _lastAccessFlushUtc = _utcNow();
                _cacheState = FileCacheState.Open;
            }

            // 启动占用可能超过新的 M：回收满足预算前允许命中、释放与回收 (写入授权被 A/R 检查拦截)
            var trimTarget = (long)(_options.MaxBytes * _options.TrimToRatio);
            if (_accountedBytes > trimTarget)
            {
                await TrimAsync(trimTarget, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task _RecoverIndexAsync(CancellationToken cancellationToken)
        {
            var dataRoot = Path.Combine(_options.Directory, "data");
            var metaRoot = Path.Combine(_options.Directory, "meta");
            _fs.CreateDirectory(dataRoot);
            _fs.CreateDirectory(metaRoot);

            // 1. 解析已提交元数据 (只有完成原子重命名的元数据才是提交标记)
            var committed = new Dictionary<string, List<(FileEntry entry, bool dataValid)>>(); // key → 候选
            long maxGeneration = 0; // 重启后推进代次种子，禁止复用旧代次路径 (R13)
            var metaTmpGarbage = new List<FileEntry>(); // 元数据临时残留 → Garbage (R15)
            foreach (var keyDir in _ListDirectoriesSafe(metaRoot))
            foreach (var metaFile in _ListFilesSafe(keyDir))
            {
                if (!metaFile.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    var tmpLength = _SafeFileLength(metaFile);
                    if (tmpLength > 0)
                    {
                        metaTmpGarbage.Add(new FileEntry(Path.GetFileName(keyDir), default, 0,
                            metaFile, "", tmpLength, 0, "", DateTime.MinValue, DateTime.MinValue,
                            false, TimeSpan.Zero, null, null) { State = FileEntryState.Garbage });
                    }
                    else
                    {
                        _TryDeleteFile(metaFile);
                    }
                    continue;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var record = _TryReadMeta(metaFile);
                if (record != null && record.Generation > maxGeneration)
                {
                    maxGeneration = record.Generation;
                }
                if (record == null)
                {
                    // 元数据损坏：移除无效元数据并记录诊断
                    _TryDeleteFile(metaFile);
                    _diagnostics.Report(new LoadError(
                        DiagnosticCodes.CacheIndexFailed, LoadStage.CacheLookup, CleanupStatus.Complete, null,
                        new Dictionary<string, object> { { "meta", Path.GetFileName(metaFile) } }));
                    continue;
                }
                var dataPath = Path.Combine(_options.Directory, record.RelativeDataPath);
                long actualLength = -1;
                if (_fs.FileExists(dataPath))
                {
                    try { actualLength = _fs.GetFileLength(dataPath); } catch { actualLength = -1; }
                }
                var valid = actualLength == record.Length;
                var entry = new FileEntry(record.Key, record.Identity, record.Generation, dataPath, metaFile,
                    valid ? record.Length : Math.Max(0, actualLength), _SafeFileLength(metaFile),
                    record.Digest, record.StoredAtUtc, record.ValidUntilUtc, record.ValidityExpires, record.Ttl,
                    record.LengthClaim, record.ShaClaim)
                {
                    LastAccessUtc = record.LastAccessUtc,
                };
                if (!committed.TryGetValue(record.Key, out var list))
                {
                    committed[record.Key] = list = new List<(FileEntry, bool)>();
                }
                list.Add((entry, valid));
            }

            // 2. 建立索引：同身份多个已提交代次取最新有效项，其余 Retired 待回收
            var accountedFiles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            lock (_state)
            {
                foreach (var pair in committed)
                {
                    var validEntries = pair.Value.Where(t => t.dataValid).ToList();
                    if (validEntries.Count > 0)
                    {
                        // 同身份多个已提交代次：按提交时间与代次取最新有效项，其余 Retired 待回收
                        var winner = validEntries
                            .OrderByDescending(t => t.entry.StoredAtUtc)
                            .ThenByDescending(t => t.entry.Generation)
                            .First();
                        winner.entry.State = FileEntryState.Ready;
                        _ready[pair.Key] = winner.entry;
                        _tracked.Add(winner.entry);
                        foreach (var other in pair.Value)
                        {
                            if (ReferenceEquals(other.entry, winner.entry)) continue;
                            other.entry.State = other.dataValid
                                ? FileEntryState.Retired
                                : FileEntryState.Garbage; // 元数据在但数据缺失/长度不符
                            _tracked.Add(other.entry);
                        }
                    }
                    else if (pair.Value.Count > 0)
                    {
                        // 无任何有效代次：不交付，按恢复规则回收
                        foreach (var other in pair.Value)
                        {
                            other.entry.State = FileEntryState.Garbage;
                            _tracked.Add(other.entry);
                        }
                    }
                }

                // 3. 数据文件没有已提交元数据 → Garbage；.part / 元数据临时残留 → 回收
                var knownDataFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in _tracked)
                {
                    knownDataFiles.Add(entry.DataPath);
                }

                foreach (var keyDir in _ListDirectoriesSafe(dataRoot))
                foreach (var dataFile in _ListFilesSafe(keyDir))
                {
                    if (knownDataFiles.Contains(dataFile)) continue;
                    var fileGeneration = _ParseGenerationSuffix(Path.GetFileName(dataFile));
                    if (fileGeneration > maxGeneration)
                    {
                        maxGeneration = fileGeneration;
                    }
                    var garbage = new FileEntry(
                        Path.GetFileName(keyDir), default, 0, dataFile, "", _SafeFileLength(dataFile), 0,
                        "", DateTime.MinValue, DateTime.MinValue, false, TimeSpan.Zero, null, null)
                    { State = FileEntryState.Garbage };
                    _tracked.Add(garbage);
                }

                foreach (var part in _ListFilesSafe(_StagingDir()))
                {
                    var garbage = new FileEntry(
                        "staging", default, 0, part, "", _SafeFileLength(part), 0,
                        "", DateTime.MinValue, DateTime.MinValue, false, TimeSpan.Zero, null, null)
                    { State = FileEntryState.Garbage };
                    _tracked.Add(garbage);
                }

                foreach (var tmpGarbage in metaTmpGarbage)
                {
                    _tracked.Add(tmpGarbage);
                }
                foreach (var entry in _tracked)
                {
                    _accountedBytes += entry.Length + entry.MetaLength;
                }
                if (maxGeneration > _generationSeed)
                {
                    _generationSeed = maxGeneration;
                }
            }

            // 清理孤立文件与垃圾 (在 S 外执行删除)
            foreach (var entry in _tracked.ToList())
            {
                if (entry.State == FileEntryState.Garbage || entry.State == FileEntryState.Retired)
                {
                    lock (_state)
                    {
                        _ScheduleDeleteNoLock(entry);
                    }
                }
            }
        }

        #endregion

        #region 获取：命中与下载 (§10.6)

        public async Task<FileLease> AcquireAsync(FileRequest request, CancellationToken cancellationToken = default)
        {
            _ValidateRequest(request);
            var key = FileKeyEncoding.Encode(request.Identity);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var waiter = new FileWaiter(Interlocked.Increment(ref _waiterSeed), cancellationToken);
                Task? barrier = null;
                var startJob = false;

                lock (_state)
                {
                    if (_cacheState != FileCacheState.Open)
                    {
                        throw new ResourceLoadException(new LoadError(
                            DiagnosticCodes.LifecycleSystemClosing, LoadStage.CacheLookup, CleanupStatus.Complete,
                            null, _ContextOf(key, request)));
                    }
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _CancelWaiterNoLock(waiter);
                    }
                    else if (_ready.TryGetValue(key, out var entry)
                             && entry.State == FileEntryState.Ready && _ValidFor(request, entry))
                    {
                        // 命中路径不发起网络调用
                        _AssertDescriptorCompatible(request, entry.Length, entry.Digest, entry.Ttl, entry.ValidityExpires);
                        entry.PinCount++;
                        entry.LastAccessUtc = _utcNow();
                        entry.DirtyAccess = true;
                        waiter.State = WaiterState.Granted;
                        waiter.Completion.TrySetResult(new FileLease(this, entry));
                        _MaybeStartAccessFlushNoLock();
                    }
                    else
                    {
                        if (_ready.TryGetValue(key, out var stale) && stale.State == FileEntryState.Ready)
                        {
                            // 过期条目停止交付；旧 pin 继续使用，lease 仍指旧代次
                            _ready.Remove(key);
                            stale.State = FileEntryState.Retired;
                            if (stale.PinCount == 0)
                            {
                                _ScheduleDeleteNoLock(stale);
                            }
                        }

                        if (_jobs.TryGetValue(key, out var job))
                        {
                            if (job.IsAccepting)
                            {
                                _AssertRequestCompatible(request, job.Request);
                                waiter.Job = job;
                                job.Waiters.Add(waiter);
                            }
                            else
                            {
                                barrier = job.Terminal.Task; // 非接收作业：等待终局后重试，不开第二个同身份下载
                            }
                        }
                        else
                        {
                            job = new DownloadJob(Interlocked.Increment(ref _jobSeed), key,
                                Interlocked.Increment(ref _generationSeed), request,
                                "fop-" + Interlocked.Increment(ref _jobSeed));
                            _jobs[key] = job;
                            waiter.Job = job;
                            job.Waiters.Add(waiter); // 必须在加入首个等待者后再启动作业
                            startJob = true;
                        }
                    }
                }

                if (barrier != null)
                {
                    await barrier.WaitWithCancellation(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                waiter.Registration = cancellationToken.Register(
                    () => { lock (_state) { _CancelWaiterNoLock(waiter); } });
                if (startJob)
                {
                    _ = RunJobAsync(job: waiter.Job!);
                }
                try
                {
                    return (FileLease)await waiter.Completion.Task.ConfigureAwait(false);
                }
                finally
                {
                    waiter.Registration.Dispose();
                }
            }
        }

        private void _CancelWaiterNoLock(FileWaiter waiter)
        {
            if (waiter.State != WaiterState.Pending)
            {
                return;
            }
            waiter.State = WaiterState.Cancelled;
            waiter.Job?.Waiters.Remove(waiter);
            waiter.Completion.TrySetCanceled();
            var job = waiter.Job;
            if (job != null && job.IsAccepting && _HasNoPendingWaiter(job))
            {
                // 全部等待者退出：作业放弃；jobs[key] 保留为非接收状态直到清理完成
                job.State = DownloadJobState.Abandoning;
                job.CancellationSource.Cancel();
            }
        }

        private static bool _HasNoPendingWaiter(DownloadJob job)
        {
            foreach (var w in job.Waiters)
            {
                if (w.State == WaiterState.Pending)
                {
                    return false;
                }
            }
            return true;
        }

        private void _FailJobWaitersNoLock(DownloadJob job, LoadError error)
        {
            foreach (var w in job.Waiters.ToArray())
            {
                if (w.State != WaiterState.Pending) continue;
                if (w.CallerToken.IsCancellationRequested)
                {
                    _CancelWaiterNoLock(w);
                }
                else
                {
                    w.State = WaiterState.Failed;
                    w.Completion.TrySetException(new ResourceLoadException(error));
                }
            }
            job.Waiters.Clear();
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

        private bool _ValidFor(FileRequest request, FileEntry entry)
        {
            if (!entry.ValidityExpires)
            {
                return true;
            }
            // TTL 内有效命中零网络；到期视为未命中，第一版重新下载
            return _utcNow() <= entry.ValidUntilUtc;
        }

        private void _AssertDescriptorCompatible(FileRequest request, long length, string digest,
            TimeSpan ttl, bool expires)
        {
            if (request.ExpectedLength is long expected && expected != length
                || request.ExpectedSha256 != null && digest.Length == 64
                && !string.Equals(request.ExpectedSha256, digest, StringComparison.OrdinalIgnoreCase))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.CacheIdentityConflict, LoadStage.CacheLookup, CleanupStatus.Complete, null,
                    _ContextOf("<unknown>", request, "conflict:descriptor")));
            }
            _AssertValidityCompatible(request, ttl, expires);
        }

        private void _AssertRequestCompatible(FileRequest incoming, FileRequest jobRequest)
        {
            if (incoming.ExpectedLength != null && jobRequest.ExpectedLength != null
                && incoming.ExpectedLength != jobRequest.ExpectedLength
                || incoming.ExpectedSha256 != null && jobRequest.ExpectedSha256 != null
                && !string.Equals(incoming.ExpectedSha256, jobRequest.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.CacheIdentityConflict, LoadStage.CacheLookup, CleanupStatus.Complete, null,
                    _ContextOf("conflict:job", incoming)));
            }
            _AssertValidityCompatible(incoming, jobRequest.Validity.Ttl,
                jobRequest.Validity.Mode == ValidityMode.ExpiresAfter);
        }

        private void _AssertValidityCompatible(FileRequest request, TimeSpan entryTtl, bool entryExpires)
        {
            var requestExpires = request.Validity.Mode == ValidityMode.ExpiresAfter;
            if (requestExpires != entryExpires
                || requestExpires && !request.Validity.Ttl.Equals(entryTtl))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.CacheIdentityConflict, LoadStage.CacheLookup, CleanupStatus.Complete, null,
                    _ContextOf("conflict:validity", request)));
            }
        }

        #endregion

        #region 下载作业 (§10.6 RunJob / RunAttempts)

        private async Task RunJobAsync(DownloadJob job)
        {
            var slotAcquired = false;
            LoadError? failure = null;
            try
            {
                await _downloadSlots.WaitAsync(job.CancellationSource.Token).ConfigureAwait(false);
                slotAcquired = true;
                await RunAttemptsAsync(job).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (job.CancellationSource.Token.IsCancellationRequested)
            {
                // 包括等待下载槽位时取消：等待者已在取消路径按各自令牌结算
                lock (_state)
                {
                    if (job.State != DownloadJobState.Succeeded)
                    {
                        job.State = DownloadJobState.Abandoning;
                    }
                    _FailJobWaitersNoLock(job, _AbandonedError());
                }
            }
            catch (ResourceLoadException rle)
            {
                failure = rle.Error;
                lock (_state)
                {
                    job.State = DownloadJobState.Failed;
                    _FailJobWaitersNoLock(job, failure);
                }
            }
            catch (Exception ex)
            {
                failure = _ClassifyUnexpected(job, ex);
                lock (_state)
                {
                    job.State = DownloadJobState.Failed;
                    _FailJobWaitersNoLock(job, failure);
                }
            }
            finally
            {
                // 关闭写句柄、清理未转交 FileEntry 的临时路径，核对残留实际长度
                await _ReconcileAndDeletePartAsync(job).ConfigureAwait(false);

                lock (_state)
                {
                    // 未写入的预留在此释放；残留字节已按 Garbage 入账
                    _reservedBytes -= job.ReservedBytes;
                    job.ReservedBytes = 0;
                    if (_jobs.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job))
                    {
                        _jobs.Remove(job.Key);
                    }
                    if (job.State != DownloadJobState.Succeeded && job.State != DownloadJobState.Failed)
                    {
                        _FailJobWaitersNoLock(job, _AbandonedError());
                    }
                    if (job.State != DownloadJobState.Succeeded)
                    {
                        job.State = DownloadJobState.Cleaned;
                    }
                    job.Terminal.TrySetResult(null!); // 清理残留已入账
                    _CheckShutdownCompleteNoLock();
                }

                if (slotAcquired)
                {
                    _downloadSlots.Release();
                }
                if (failure != null)
                {
                    _diagnostics.Report(failure);
                }
            }
        }

        private async Task RunAttemptsAsync(DownloadJob job)
        {
            var request = job.Request;
            var metadata = (long)_options.MetadataAllowanceBytes;
            var maxAttempts = 1 + _network.MaxRetries;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (_JobAbandoning(job))
                {
                    return;
                }
                job.Attempt = attempt;

                LoadError? failure = null;
                try
                {
                    // 已知长度的整文件预算在打开网络前预留：entry_too_large 时网络正文不开始
                    if (request.ExpectedLength is long expected)
                    {
                        if (expected + metadata > _options.MaxBytes)
                        {
                            throw new ResourceLoadException(new LoadError(
                                DiagnosticCodes.CacheEntryTooLarge, LoadStage.ReserveCapacity,
                                CleanupStatus.Complete, null,
                                _ContextOf(job.Key, request, $"expected={expected}, max={_options.MaxBytes}")));
                        }
                        await EnsureReservationAsync(job, expected + metadata).ConfigureAwait(false);
                    }
                    await EnsureReservationAsync(job, metadata).ConfigureAwait(false);

                    var partPath = Path.Combine(_StagingDir(), "job-" + job.Id + "-try" + attempt + ".part");
                    job.PartPath = partPath;
                    job.WrittenBytes = 0;

                    TransportResponse? response = null;
                    Stream? writeStream = null;
                    long? contentLengthClaim = null;
                    long written = 0;
                    string hashHex;
                    try
                    {
                        lock (_state) { job.State = DownloadJobState.Opening; }
                        if (_transport == null)
                        {
                            throw new ResourceLoadException(new LoadError(
                                DiagnosticCodes.InternalUnexpected, LoadStage.Download,
                                CleanupStatus.Complete, null, _ContextOf(job.Key, request, "no-transport")));
                        }
                        response = await _transport.OpenReadAsync(request, job.CancellationSource.Token)
                            .ConfigureAwait(false);
                        contentLengthClaim = response.ContentLength;
                        if (contentLengthClaim is long contentLength)
                        {
                            if (request.ExpectedLength is long expectedLength && contentLength != expectedLength)
                            {
                                throw new ResourceLoadException(new LoadError(
                                    DiagnosticCodes.NetworkProtocolError, LoadStage.Download,
                                    CleanupStatus.Complete, null,
                                    _ContextOf(job.Key, request,
                                        $"content-length={contentLength}, expected={expectedLength}")));
                            }
                            if (contentLength + metadata > _options.MaxBytes)
                            {
                                throw new ResourceLoadException(new LoadError(
                                    DiagnosticCodes.CacheEntryTooLarge, LoadStage.ReserveCapacity,
                                    CleanupStatus.Complete, null,
                                    _ContextOf(job.Key, request, $"content-length={contentLength}")));
                            }
                            await EnsureReservationAsync(job, contentLength + metadata).ConfigureAwait(false);
                        }

                        writeStream = _fs.CreateWrite(partPath);
                        lock (_state) { job.State = DownloadJobState.Downloading; }
                        var buffer = new byte[_options.ChunkBytes];
                        using (var sha = SHA256.Create())
                        {
                            while (true)
                            {
                                job.CancellationSource.Token.ThrowIfCancellationRequested();
                                var read = await response.Body.ReadAsync(buffer, 0, buffer.Length,
                                    job.CancellationSource.Token).ConfigureAwait(false);
                                if (read <= 0)
                                {
                                    break;
                                }
                                if (request.ExpectedLength is long exp && written + read > exp)
                                {
                                    throw new ResourceLoadException(new LoadError(
                                        DiagnosticCodes.CacheIntegrityFailed, LoadStage.ValidateContent,
                                        CleanupStatus.Complete, null,
                                        _ContextOf(job.Key, request, "written-exceeds-expected")));
                                }
                                // 未知长度逐块申请预算：写入前已有至少本块预留
                                await EnsureReservationAsync(job, read + metadata).ConfigureAwait(false); // R10
                                await writeStream.WriteAsync(buffer, 0, read, job.CancellationSource.Token)
                                    .ConfigureAwait(false);
                                await writeStream.FlushAsync(job.CancellationSource.Token).ConfigureAwait(false);
                                sha.TransformBlock(buffer, 0, read, buffer, 0);
                                written += read;
                                long chunk = read;
                                lock (_state)
                                {
                                    // 预留转为已落盘计量，总计量不增加
                                    _accountedBytes += chunk;
                                    _reservedBytes -= chunk;
                                    job.ReservedBytes -= chunk;
                                    job.WrittenBytes += chunk;
                                }
                            }
                            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            hashHex = _ToHex(sha.Hash!);
                        }
                    }
                    finally
                    {
                        if (writeStream != null)
                        {
                            await _DisposeQuietlyAsync(writeStream).ConfigureAwait(false);
                        }
                        if (response != null)
                        {
                            await response.DisposeAsync().ConfigureAwait(false);
                        }
                    }

                    lock (_state) { job.State = DownloadJobState.Verifying; }
                    if (contentLengthClaim is long claimed && written != claimed)
                    {
                        throw new ResourceLoadException(new LoadError(
                            DiagnosticCodes.CacheIntegrityFailed, LoadStage.ValidateContent,
                            CleanupStatus.Complete, null,
                            _ContextOf(job.Key, request, $"written={written}, content-length={claimed}")));
                    }
                    if (request.ExpectedLength is long expect && written != expect)
                    {
                        throw new ResourceLoadException(new LoadError(
                            DiagnosticCodes.CacheIntegrityFailed, LoadStage.ValidateContent,
                            CleanupStatus.Complete, null,
                            _ContextOf(job.Key, request, $"written={written}, expected={expect}")));
                    }
                    if (request.ExpectedSha256 != null
                        && !string.Equals(request.ExpectedSha256, hashHex, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ResourceLoadException(new LoadError(
                            DiagnosticCodes.CacheIntegrityFailed, LoadStage.ValidateContent,
                            CleanupStatus.Complete, null, _ContextOf(job.Key, request, "sha256-mismatch")));
                    }

                    if (_JobAbandoning(job) || !_IsOpen())
                    {
                        return; // 候选 .part 由 finally 的清理路径回收
                    }

                    lock (_state) { job.State = DownloadJobState.Committing; }
                    var committedEntry = await _CommitAsync(job, partPath, written, hashHex).ConfigureAwait(false);
                    if (committedEntry == null)
                    {
                        return; // 提交期放弃：已提交文件按 Garbage 入账并安排删除
                    }
                    return; // 成功
                }
                catch (OperationCanceledException) when (job.CancellationSource.Token.IsCancellationRequested)
                {
                    throw; // 放弃：由 RunJob 外层统一收尾
                }
                catch (ResourceLoadException rle)
                {
                    failure = rle.Error;
                }
                catch (Exception ex)
                {
                    failure = _ClassifyUnexpected(job, ex);
                }

                // 尝试失败：以文件实际长度重新核对 A，删除临时文件或保留为 Garbage
                await _ReconcileAndDeletePartAsync(job).ConfigureAwait(false);

                if (_JobAbandoning(job))
                {
                    return;
                }
                if (_IsTransient(failure!) && attempt < maxAttempts)
                {
                    // 有界指数退避 + 随机抖动；作业取消时中断
                    double delay;
                    lock (RandomGate)
                    {
                        delay = _network.RetryBaseDelay.TotalSeconds * Math.Pow(2, attempt - 1)
                                + RandomSource.NextDouble() * 0.25;
                    }
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delay), job.CancellationSource.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    continue;
                }

                lock (_state)
                {
                    job.State = DownloadJobState.Failed;
                    _FailJobWaitersNoLock(job, failure!);
                }
                _diagnostics.Report(failure!);
                return;
            }
        }

        /// <summary> 数据文件完成 → 元数据原子重命名 (提交标记) → S 内登记 Ready 并发放 </summary>
        private async Task<FileEntry?> _CommitAsync(DownloadJob job, string partPath, long written, string hashHex)
        {
            var request = job.Request;
            var dataPath = _DataPath(request.Identity, job.Key, job.Generation);
            var metaPath = _MetaPath(job.Key, job.Generation);
            var metaTmp = metaPath + ".tmp-" + job.Id;

            _fs.CreateDirectory(Path.GetDirectoryName(dataPath)!);
            _fs.CreateDirectory(Path.GetDirectoryName(metaPath)!);

            // 数据文件不可变代次路径，禁止覆盖旧代次；同卷移动。
            // 移动后仍登记在 PartPath：元数据提交前的失败由统一清理路径回收该候选文件。
            // 提交段的 I/O 失败归类为 commit_failed (残留由清理路径核对)，不误报 network
            long metaLength;
            DateTime storedUtc;
            DateTime validUntil;
            try
            {
                _fs.MoveFile(partPath, dataPath, overwrite: false);
                job.PartPath = dataPath;

                storedUtc = _utcNow();
                validUntil = request.Validity.Mode == ValidityMode.ExpiresAfter
                    ? storedUtc + request.Validity.Ttl
                    : DateTime.MinValue;
                job.MetaTmpPath = metaTmp;
                metaLength = _WriteMeta(metaTmp, job.Key, request.Identity, job.Generation,
                    _RelativeDataPath(request.Identity, job.Key, job.Generation), written, hashHex, storedUtc,
                    validUntil, storedUtc, request.Validity.Mode == ValidityMode.ExpiresAfter, request.Validity.Ttl,
                    request.ExpectedLength, request.ExpectedSha256);
                _fs.MoveFile(metaTmp, metaPath, overwrite: false);
                job.MetaTmpPath = null;
            }
            catch (ResourceLoadException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.CacheCommitFailed, LoadStage.ValidateContent,
                    CleanupStatus.Complete, ex,
                    new Dictionary<string, object> { { "key", job.Key } }));
            }

            var abandoned = false;
            lock (_state)
            {
                if (job.State == DownloadJobState.Abandoning || _cacheState != FileCacheState.Open)
                {
                    abandoned = true;
                }
                else
                {
                    var entry = new FileEntry(job.Key, request.Identity, job.Generation, dataPath, metaPath,
                        written, metaLength, hashHex, storedUtc, validUntil,
                        request.Validity.Mode == ValidityMode.ExpiresAfter, request.Validity.Ttl,
                        request.ExpectedLength, request.ExpectedSha256)
                    {
                        State = FileEntryState.Ready,
                        LastAccessUtc = storedUtc,
                    };
                    _tracked.Add(entry);

                    // 退位任何旧 Ready 代次
                    if (_ready.TryGetValue(job.Key, out var prior) && !ReferenceEquals(prior, entry))
                    {
                        _ready.Remove(job.Key);
                        prior.State = FileEntryState.Retired;
                        if (prior.PinCount == 0)
                        {
                            _ScheduleDeleteNoLock(prior);
                        }
                    }
                    _ready[job.Key] = entry;

                    var granted = 0;
                    foreach (var w in job.Waiters.ToArray())
                    {
                        if (w.State != WaiterState.Pending) continue;
                        if (w.CallerToken.IsCancellationRequested)
                        {
                            _CancelWaiterNoLock(w);
                            continue;
                        }
                        // 先 pin 再交付；取消先发生则不发放
                        entry.PinCount++;
                        w.State = WaiterState.Granted;
                        w.Completion.TrySetResult(new FileLease(this, entry));
                        granted++;
                    }
                    job.Waiters.Clear();
                    job.PartPath = null; // 已转交 FileEntry 所有
                    job.MetaTmpPath = null;

                    // 元数据落盘计量 (数据字节已在写入时从 R 转 A)；释放剩余预留
                    _accountedBytes += metaLength;
                    _reservedBytes -= metaLength;
                    job.ReservedBytes -= metaLength;
                    if (job.ReservedBytes > 0)
                    {
                        _reservedBytes -= job.ReservedBytes;
                        job.ReservedBytes = 0;
                    }

                    if (granted == 0)
                    {
                        // 无人接收的完整文件按缓存策略回收
                        _ready.Remove(job.Key);
                        entry.State = FileEntryState.Retired;
                        if (entry.PinCount == 0)
                        {
                            _ScheduleDeleteNoLock(entry);
                        }
                    }
                    job.State = DownloadJobState.Succeeded;
                }
            }

            if (abandoned)
            {
                // 已提交文件作为 Garbage 计入容量并回收。
                // 数据字节已在写入时计入 A (R→A)，此处只补计元数据，避免双重计量
                var metaLen = _SafeFileLength(metaPath);
                var garbage = new FileEntry(job.Key, request.Identity, job.Generation, dataPath, metaPath,
                    written, metaLen, hashHex, storedUtc, DateTime.MinValue, false, TimeSpan.Zero, null, null)
                { State = FileEntryState.Garbage };
                lock (_state)
                {
                    _accountedBytes += metaLen;
                    job.PartPath = null;
                    job.MetaTmpPath = null;
                    _tracked.Add(garbage);
                    _ScheduleDeleteNoLock(garbage);
                }
                return null;
            }
            return _ready.TryGetValue(job.Key, out var ready) ? ready : null;
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
                    return false; // 地址错误、404、权限、磁盘不足、容量、校验失败不自动重试
            }
        }

        private LoadError _ClassifyUnexpected(DownloadJob job, Exception ex)
        {
            string code;
            if (FileSystemErrorClassifier.IsDiskFull(ex))
            {
                code = DiagnosticCodes.CacheDiskFull; // 逻辑预算充足但磁盘满：不误报 network
            }
            else if (FileSystemErrorClassifier.IsAccessDenied(ex))
            {
                code = DiagnosticCodes.FileAccessDenied;
            }
            else if (ex is IOException)
            {
                code = job.WrittenBytes > 0
                    ? DiagnosticCodes.NetworkInterrupted
                    : DiagnosticCodes.NetworkConnectFailed;
            }
            else if (ex is OperationCanceledException)
            {
                code = DiagnosticCodes.NetworkTimeout; // 底层超时取消而非作业取消
            }
            else
            {
                code = DiagnosticCodes.InternalUnexpected;
            }
            return new LoadError(code, LoadStage.Download, CleanupStatus.Complete, ex,
                _ContextOf(job.Key, job.Request, null));
        }

        private bool _JobAbandoning(DownloadJob job)
        {
            lock (_state)
            {
                return job.State == DownloadJobState.Abandoning || _cacheState != FileCacheState.Open;
            }
        }

        private bool _IsOpen()
        {
            lock (_state)
            {
                return _cacheState == FileCacheState.Open;
            }
        }

        private static LoadError _AbandonedError()
        {
            return new LoadError(DiagnosticCodes.LifecycleSystemClosing, LoadStage.Download,
                CleanupStatus.Complete);
        }

        #endregion

        #region 容量记账与回收 (§10.5 / §10.6 EnsureReservation)

        /// <summary>
        /// 确保作业至少持有 requiredRemaining 的未写预留；预算不足时按"过期优先、访问时间"
        /// 回收未 pin 条目。无可删候选时抛 cache.capacity_exceeded。回收目标是优化，不构成额外失败条件。
        /// </summary>
        internal async Task EnsureReservationAsync(DownloadJob job, long requiredRemaining)
        {
            while (true)
            {
                FileEntry? candidate = null;
                List<KeyValuePair<string, long>>? files = null;
                lock (_state)
                {
                    if (job.State == DownloadJobState.Abandoning || _cacheState != FileCacheState.Open)
                    {
                        throw new ResourceLoadException(_AbandonedError());
                    }
                    var delta = requiredRemaining - job.ReservedBytes;
                    if (delta <= 0)
                    {
                        return;
                    }
                    if (_accountedBytes + _reservedBytes + delta <= _options.MaxBytes)
                    {
                        _reservedBytes += delta;
                        job.ReservedBytes += delta;
                        return;
                    }
                    candidate = _PickEvictionCandidateNoLock();
                    if (candidate == null)
                    {
                        throw new ResourceLoadException(new LoadError(
                            DiagnosticCodes.CacheCapacityExceeded, LoadStage.ReserveCapacity,
                            CleanupStatus.Complete, null,
                            new Dictionary<string, object>
                            {
                                { "key", job.Key },
                                { "accounted", _accountedBytes },
                                { "reserved", _reservedBytes },
                                { "required", delta },
                                { "max", _options.MaxBytes },
                            }));
                    }
                    candidate.State = FileEntryState.Deleting; // 串行标记，停止新发放
                    files = candidate.OwnedFiles();
                }

                var report = await _DeleteFilesOutsideAsync(files!).ConfigureAwait(false);
                _FinishDeleteTransaction(candidate!, report);
            }
        }

        private FileEntry? _PickEvictionCandidateNoLock()
        {
            var now = _utcNow();
            FileEntry? best = null;
            var bestRank = long.MaxValue;
            foreach (var entry in _tracked)
            {
                if (entry.PinCount != 0) continue; // pin 文件始终保留
                if (entry.State != FileEntryState.Ready && entry.State != FileEntryState.Retired
                    && entry.State != FileEntryState.Garbage) continue;
                if (entry.NextDeleteRetryAt > _monotonicNow()) continue; // 删除退避中
                var rank = (entry.IsExpired(now) ? 0L : 1L) * 4_000_000_000_000_000_000L
                           + entry.LastAccessUtc.Ticks; // 过期优先，其次最久未访问
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = entry;
                }
            }
            return best;
        }

        /// <summary> 单个路径的删除结果：Bytes 为本次事务前该路径的已核算字节 (R16) </summary>
        private readonly struct FileDeleteResult
        {
            public readonly string Path;
            public readonly long Bytes;
            public readonly bool Deleted;

            public FileDeleteResult(string path, long bytes, bool deleted)
            {
                Path = path;
                Bytes = bytes;
                Deleted = deleted;
            }
        }

        private sealed class DeleteReport
        {
            public readonly List<FileDeleteResult> Results = new List<FileDeleteResult>();
            public Exception? FirstError;

            public bool AllDeleted
            {
                get
                {
                    foreach (var result in Results)
                    {
                        if (!result.Deleted)
                        {
                            return false;
                        }
                    }
                    return true;
                }
            }

            public long FreedBytes
            {
                get
                {
                    long sum = 0;
                    foreach (var result in Results)
                    {
                        if (result.Deleted)
                        {
                            sum += result.Bytes;
                        }
                    }
                    return sum;
                }
            }
        }

        /// <summary> 在 S 外执行磁盘删除；只把"确认不存在"视为成功 </summary>
        private Task<DeleteReport> _DeleteFilesOutsideAsync(List<KeyValuePair<string, long>> files)
        {
            var report = new DeleteReport();
            foreach (var file in files)
            {
                try
                {
                    if (!_fs.FileExists(file.Key))
                    {
                        report.Results.Add(new FileDeleteResult(file.Key, file.Value, true)); // 已不存在视为成功
                        continue;
                    }
                    _fs.DeleteFile(file.Key);
                    report.Results.Add(new FileDeleteResult(file.Key, file.Value, !_fs.FileExists(file.Key)));
                }
                catch (Exception ex)
                {
                    report.Results.Add(new FileDeleteResult(file.Key, file.Value, false));
                    report.FirstError ??= ex;
                }
            }
            return Task.FromResult(report);
        }

        /// <summary>
        /// 按路径分别记账 (R16)：确认删除的路径扣减并把该路径计量清零，每份字节只扣一次；
        /// 失败残留转 Garbage，剩余计量 = 未清零路径之和，不从合计量倒推。
        /// </summary>
        private void _FinishDeleteTransaction(FileEntry entry, DeleteReport report)
        {
            lock (_state)
            {
                foreach (var result in report.Results)
                {
                    if (!result.Deleted)
                    {
                        continue;
                    }
                    _accountedBytes -= result.Bytes;
                    if (string.Equals(result.Path, entry.DataPath, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Length = 0;
                    }
                    else if (string.Equals(result.Path, entry.MetaPath, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.MetaLength = 0;
                    }
                }
                if (report.AllDeleted)
                {
                    if (_ready.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                    {
                        _ready.Remove(entry.Key);
                    }
                    _tracked.Remove(entry);
                }
                else
                {
                    // 失败残留：保留占用与错误，退避后重试；不谎称容量充足
                    entry.State = FileEntryState.Garbage;
                    entry.DeleteError = new LoadError(
                        DiagnosticCodes.CacheDeleteFailed, LoadStage.CacheEvict,
                        CleanupStatus.Complete, report.FirstError,
                        new Dictionary<string, object> { { "key", entry.Key } });
                    entry.NextDeleteRetryAt = _monotonicNow() + 5.0;
                    _diagnostics.Report(entry.DeleteError);
                }
                _CheckShutdownCompleteNoLock();
            }
        }

        /// <summary>
        /// 失败/部分写入后的统一清理 (R15)：核对 PartPath 与 MetaTmpPath 的实际残留，
        /// 删除或转为 Garbage；数据部分按实际长度重核 A，元数据临时文件此前未计量、残留时入账。
        /// </summary>
        private async Task _ReconcileAndDeletePartAsync(DownloadJob job)
        {
            string? partPath;
            string? metaTmpPath;
            long written;
            lock (_state)
            {
                partPath = job.PartPath;
                metaTmpPath = job.MetaTmpPath;
                job.PartPath = null;
                job.MetaTmpPath = null;
                written = job.WrittenBytes;
                job.WrittenBytes = 0;
            }

            if (partPath != null)
            {
                var exists = _fs.FileExists(partPath);
                var actual = exists ? _SafeFileLength(partPath) : 0;
                var report = await _DeleteFilesOutsideAsync(
                    new List<KeyValuePair<string, long>> { new KeyValuePair<string, long>(partPath, actual) })
                    .ConfigureAwait(false);
                lock (_state)
                {
                    // 先按实际长度重核 (部分写入也可能落盘)，再扣除已删除部分
                    _accountedBytes += actual - written;
                    if (report.AllDeleted)
                    {
                        _accountedBytes -= actual;
                    }
                    else if (exists)
                    {
                        _tracked.Add(new FileEntry("staging", default, 0, partPath, "", actual, 0,
                            "", DateTime.MinValue, DateTime.MinValue, false, TimeSpan.Zero, null, null)
                        { State = FileEntryState.Garbage });
                    }
                }
            }

            if (metaTmpPath != null)
            {
                var exists = _fs.FileExists(metaTmpPath);
                var actual = exists ? _SafeFileLength(metaTmpPath) : 0;
                var report = await _DeleteFilesOutsideAsync(
                    new List<KeyValuePair<string, long>> { new KeyValuePair<string, long>(metaTmpPath, actual) })
                    .ConfigureAwait(false);
                lock (_state)
                {
                    if (!report.AllDeleted && exists)
                    {
                        // 元数据临时文件此前未计量：残留入账，不静默遗漏 (R15)
                        _accountedBytes += actual;
                        _tracked.Add(new FileEntry("staging", default, 0, metaTmpPath, "", actual, 0,
                            "", DateTime.MinValue, DateTime.MinValue, false, TimeSpan.Zero, null, null)
                        { State = FileEntryState.Garbage });
                    }
                }
            }
        }

        private static long _ParseGenerationSuffix(string fileName)
        {
            var idx = fileName.LastIndexOf('-');
            if (idx < 0 || idx + 1 >= fileName.Length)
            {
                return 0;
            }
            return long.TryParse(fileName.Substring(idx + 1), out var generation) && generation > 0
                ? generation
                : 0;
        }

        private FileEntry _PlaceholderEntry(string path, long length)
        {
            return new FileEntry("staging", default, 0, path, "", length, 0,
                "", DateTime.MinValue, DateTime.MinValue, false, TimeSpan.Zero, null, null);
        }

        #endregion

        #region 租约归还、失效与主动回收

        internal void ReleaseLease(FileEntry entry)
        {
            lock (_state)
            {
                if (entry.PinCount <= 0)
                {
                    _diagnostics.Report(new LoadError(
                        DiagnosticCodes.InternalConsistency, LoadStage.CacheEvict,
                        CleanupStatus.Unknown, null, new Dictionary<string, object> { { "key", entry.Key } }));
                    return;
                }
                entry.PinCount--;
                if (entry.PinCount == 0 && entry.State == FileEntryState.Retired)
                {
                    _ScheduleDeleteNoLock(entry);
                }
                _CheckShutdownCompleteNoLock();
            }
        }

        /// <summary> 将具体代次标记 Retired (调用方随后 Dispose 才允许回收)；不删除 pin 文件 </summary>
        public void Invalidate(FileLease lease)
        {
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            if (lease.IsDisposed)
            {
                throw new ObjectDisposedException(nameof(FileLease));
            }
            lock (_state)
            {
                var entry = lease.Entry;
                if (_ready.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                {
                    _ready.Remove(entry.Key);
                }
                if (entry.State == FileEntryState.Ready)
                {
                    entry.State = FileEntryState.Retired;
                    if (entry.PinCount == 0)
                    {
                        _ScheduleDeleteNoLock(entry);
                    }
                }
            }
        }

        /// <summary>
        /// 回收到 targetBytes (期望清理后 A+R 不超过的数值，0 到 MaxBytes)。
        /// 只删除当前可删除项；ct 只取消调用者等待，已标记 Deleting 的事务仍完成计量。
        /// </summary>
        public async Task<TrimResult> TrimAsync(long targetBytes, CancellationToken cancellationToken = default)
        {
            if (targetBytes < 0 || targetBytes > _options.MaxBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(targetBytes));
            }
            var result = new TrimResult();
            while (true)
            {
                FileEntry? candidate = null;
                List<KeyValuePair<string, long>>? files = null;
                lock (_state)
                {
                    if (_accountedBytes + _reservedBytes <= targetBytes)
                    {
                        break;
                    }
                    candidate = _PickEvictionCandidateNoLock();
                    if (candidate == null)
                    {
                        break;
                    }
                    candidate.State = FileEntryState.Deleting;
                    files = candidate.OwnedFiles();
                }

                var report = await _DeleteFilesOutsideAsync(files!).ConfigureAwait(false);
                result.FreedBytes += report.FreedBytes;
                _FinishDeleteTransaction(candidate!, report);
                if (!report.AllDeleted)
                {
                    result.Errors.Add(candidate!.DeleteError!);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            lock (_state)
            {
                result.RemainingBytes = _accountedBytes + _reservedBytes;
                result.TargetReached = result.RemainingBytes <= targetBytes;
            }
            return result;
        }

        /// <summary> 独立下载导出：目标文件归调用方，不登记为自动淘汰缓存 </summary>
        public async Task DownloadToAsync(
            Uri source, string destination, TransportContext? transportContext = null,
            CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrEmpty(destination)) throw new ArgumentException("destination 不能为空");
            var fullDestination = Path.GetFullPath(destination);
            var fullRoot = Path.GetFullPath(_options.Directory);
            if (fullDestination.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.CachePathInvalid, LoadStage.Download, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "destination", fullDestination } }));
            }

            var request = new FileRequest(new FileIdentity("export", "direct", "1", ""),
                source, transportContext, FileValidity.Immutable);
            var maxAttempts = 1 + _network.MaxRetries;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await _DownloadDirectAsync(request, fullDestination, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (ResourceLoadException rle) when (_IsTransient(rle.Error) && attempt < maxAttempts)
                {
                    // 指数退避后重试
                }
            }
        }

        private async Task _DownloadDirectAsync(
            FileRequest request, string destination, CancellationToken cancellationToken)
        {
            if (_transport == null)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InternalUnexpected, LoadStage.Download,
                    CleanupStatus.Complete, null, new Dictionary<string, object> { { "fact", "no-transport" } }));
            }
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                _fs.CreateDirectory(directory);
            }
            var response = await _transport.OpenReadAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var write = _fs.CreateWrite(destination))
                {
                    var buffer = new byte[_options.ChunkBytes];
                    while (true)
                    {
                        var read = await response.Body.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                            .ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }
                        await write.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await response.DisposeAsync().ConfigureAwait(false);
            }
        }

        #endregion

        #region 访问时间持久化与关闭

        private void _MaybeStartAccessFlushNoLock()
        {
            if (_accessFlushInFlight || _utcNow() - _lastAccessFlushUtc < AccessFlushInterval)
            {
                return;
            }
            _accessFlushInFlight = true;
            _ = _FlushAccessAsync();
        }

        /// <summary>
        /// 访问时间批量持久化：更新一个元数据时先持有内部 pin、按同一预算预留临时元数据空间，
        /// 写完后原子替换；没有空间时可暂缓，不能为记录一次命中触发加载失败 (§10.7)。
        /// </summary>
        private async Task _FlushAccessAsync()
        {
            try
            {
                List<FileEntry> dirty;
                lock (_state)
                {
                    _lastAccessFlushUtc = _utcNow();
                    dirty = _tracked.Where(e => e.DirtyAccess && e.State == FileEntryState.Ready).ToList();
                    foreach (var entry in dirty)
                    {
                        entry.PinCount++; // 内部 pin 防止事务期间被删除
                    }
                }

                foreach (var entry in dirty)
                {
                    var allowed = false;
                    lock (_state)
                    {
                        var allowance = (long)_options.MetadataAllowanceBytes;
                        if (_accountedBytes + _reservedBytes + allowance <= _options.MaxBytes)
                        {
                            _reservedBytes += allowance;
                            allowed = true;
                        }
                    }
                    if (!allowed)
                    {
                        lock (_state) { entry.PinCount--; }
                        continue; // 暂缓，不失败
                    }

                    try
                    {
                        var metaTmp = entry.MetaPath + ".access.tmp";
                        var metaLength = _WriteMeta(metaTmp, entry.Key, entry.Identity, entry.Generation,
                            _RelativeDataPath(entry.Identity, entry.Key, entry.Generation), entry.Length,
                            entry.Digest, entry.StoredAtUtc, entry.ValidUntilUtc, entry.LastAccessUtc,
                            entry.ValidityExpires, entry.Ttl, entry.LengthClaim, entry.ShaClaim);
                        _fs.MoveFile(metaTmp, entry.MetaPath, overwrite: true); // 原子替换本代次元数据
                        lock (_state)
                        {
                            _accountedBytes += metaLength - entry.MetaLength;
                            entry.MetaLength = metaLength;
                            entry.DirtyAccess = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        _diagnostics.Report(new LoadError(
                            DiagnosticCodes.CacheIndexFailed, LoadStage.CacheLookup,
                            CleanupStatus.Complete, ex, new Dictionary<string, object> { { "key", entry.Key } }));
                    }
                    finally
                    {
                        var allowance = (long)_options.MetadataAllowanceBytes;
                        lock (_state)
                        {
                            _reservedBytes -= allowance;
                            entry.PinCount--;
                        }
                    }
                }
            }
            finally
            {
                lock (_state)
                {
                    _accessFlushInFlight = false;
                    _CheckShutdownCompleteNoLock();
                }
            }
        }

        /// <summary>
        /// 停止新租约，取消并排空下载，等待 pin 与删除事务归零后释放目录锁。
        /// 实际关闭是唯一且不可被调用者令牌中断的核心流程 (R11)；每次调用只以自己的令牌等待。
        /// </summary>
        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
            {
                _shutdownCoreTask = _ShutdownCoreAsync();
            }
            return _shutdownCoreTask.WaitWithCancellation(cancellationToken);
        }

        private async Task _ShutdownCoreAsync()
        {
            lock (_state)
            {
                if (_cacheState == FileCacheState.Open)
                {
                    _cacheState = FileCacheState.Closing;
                    foreach (var job in _jobs.Values)
                    {
                        job.CancellationSource.Cancel();
                    }
                    _CheckShutdownCompleteNoLock();
                }
            }

            await _shutdownTcs.Task.ConfigureAwait(false); // 核心流程不接受调用者取消

            // 正常关闭时批量持久化访问时间
            if (!_accessFlushInFlight)
            {
                await _FlushAccessAsync().ConfigureAwait(false);
            }

            lock (_state)
            {
                _cacheState = FileCacheState.Closed;
            }
            _lockHandle?.Dispose();
            _lockHandle = null;
        }

        private void _CheckShutdownCompleteNoLock()
        {
            if (_cacheState != FileCacheState.Closing)
            {
                return;
            }
            if (_jobs.Count > 0 || _activeDeletes > 0 || _accessFlushInFlight)
            {
                return;
            }
            foreach (var entry in _tracked)
            {
                if (entry.PinCount != 0)
                {
                    return; // 等待 lease 归还
                }
            }
            _shutdownTcs.TrySetResult(null!);
        }

        #endregion

        #region 快照与路径

        public FileCacheSnapshot GetSnapshot()
        {
            lock (_state)
            {
                var snapshot = new FileCacheSnapshot
                {
                    State = _cacheState,
                    AccountedBytes = _accountedBytes,
                    ReservedBytes = _reservedBytes,
                    MaxBytes = _options.MaxBytes,
                    Jobs = _jobs.Count,
                };
                foreach (var entry in _tracked)
                {
                    snapshot.Pins += entry.PinCount;
                    snapshot.FileRows.Add(new FileRow(entry.Key, entry.Generation, entry.State,
                        entry.Length + entry.MetaLength, entry.PinCount));
                }
                return snapshot;
            }
        }

        private string _StagingDir()
        {
            return Path.Combine(_options.Directory, "staging");
        }

        /// <summary> 数据文件：不可变代次路径；自定义映射只影响目录段且必须通过边界校验 </summary>
        private string _DataPath(FileIdentity identity, string key, long generation)
        {
            string relativeDir;
            if (_options.ResolvePath != null)
            {
                relativeDir = _ValidateRelativeSegment(_options.ResolvePath(identity, generation));
            }
            else
            {
                relativeDir = Path.Combine("data", key);
            }
            return Path.Combine(_options.Directory, relativeDir, key + "-" + generation);
        }

        private string _MetaPath(string key, long generation)
        {
            return Path.Combine(_options.Directory, "meta", key, key + "-" + generation + ".meta");
        }

        private string _RelativeDataPath(FileIdentity identity, string key, long generation)
        {
            var dataPath = _DataPath(identity, key, generation);
            var root = Path.GetFullPath(_options.Directory);
            var full = Path.GetFullPath(dataPath);
            return full.Substring(root.Length + 1); // 相对根目录存储，便于迁移
        }

        /// <summary> 边界校验：拒绝绝对路径、.. 跳出、根目录本身与非法字符 (§10.7) </summary>
        private string _ValidateRelativeSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.CachePathInvalid, LoadStage.CacheLookup, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "fact", "empty-segment" } }));
            }
            if (segment.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || Path.IsPathRooted(segment))
            {
                throw new ResourceLoadException(_PathInvalid(segment, "rooted-or-invalid"));
            }
            var normalized = Path.GetFullPath(Path.Combine(_options.Directory, segment));
            var root = Path.GetFullPath(_options.Directory);
            if (!normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ResourceLoadException(_PathInvalid(segment, "escapes-root"));
            }
            return segment;
        }

        private static LoadError _PathInvalid(string segment, string reason)
        {
            return new LoadError(
                DiagnosticCodes.CachePathInvalid, LoadStage.CacheLookup, CleanupStatus.Complete, null,
                new Dictionary<string, object> { { "segment", segment }, { "reason", reason } });
        }

        private void _ScheduleDeleteNoLock(FileEntry entry)
        {
            if (entry.State == FileEntryState.Deleting)
            {
                return;
            }
            if (_ready.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                _ready.Remove(entry.Key);
            }
            entry.State = FileEntryState.Deleting;
            _activeDeletes++;
            var files = entry.OwnedFiles();
            _ = Task.Run(async () =>
            {
                var report = await _DeleteFilesOutsideAsync(files).ConfigureAwait(false);
                lock (_state)
                {
                    _activeDeletes--;
                    _FinishDeleteTransaction(entry, report);
                }
            });
        }

        #endregion

        #region 元数据读写

        private long _WriteMeta(string path, string key, FileIdentity identity, long generation,
            string relativeDataPath, long length, string digest, DateTime storedAtUtc, DateTime validUntilUtc,
            DateTime lastAccessUtc, bool validityExpires, TimeSpan ttl, long? lengthClaim, string? shaClaim)
        {
            using var buffer = new MemoryStream();
            using (var bufferWriter = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                _SerializeMeta(bufferWriter, key, identity, generation, relativeDataPath, length, digest,
                    storedAtUtc, validUntilUtc, lastAccessUtc, validityExpires, ttl, lengthClaim, shaClaim);
            }
            if (buffer.Length > _options.MetadataAllowanceBytes)
            {
                // 每文件最大 allowance：超出拒绝提交并清理，不隐式突破预算 (R14)
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.CacheCommitFailed, LoadStage.ValidateContent,
                    CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "key", key },
                        { "metaBytes", buffer.Length },
                        { "allowance", _options.MetadataAllowanceBytes },
                    }));
            }
            using var stream = _fs.CreateWrite(path);
            stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
            stream.Flush();
            return _SafeFileLength(path);
        }

        private static void _SerializeMeta(BinaryWriter writer, string key, FileIdentity identity, long generation,
            string relativeDataPath, long length, string digest, DateTime storedAtUtc, DateTime validUntilUtc,
            DateTime lastAccessUtc, bool validityExpires, TimeSpan ttl, long? lengthClaim, string? shaClaim)
        {
            writer.Write(MetaMagic);
            writer.Write(MetaSchemaVersion);
            writer.Write(key);
            writer.Write(identity.Namespace);
            writer.Write(identity.ArtifactId);
            writer.Write(identity.Revision);
            writer.Write(identity.Variant);
            writer.Write(generation);
            writer.Write(relativeDataPath);
            writer.Write(length);
            writer.Write(digest ?? "");
            writer.Write(storedAtUtc.Ticks);
            writer.Write(validUntilUtc.Ticks);
            writer.Write(lastAccessUtc.Ticks);
            writer.Write(validityExpires);
            writer.Write(ttl.Ticks);
            writer.Write(lengthClaim ?? -1L);
            writer.Write(shaClaim ?? "");
            writer.Flush();
        }

        private sealed class MetaRecord
        {
            public string Key = "";
            public FileIdentity Identity;
            public long Generation;
            public string RelativeDataPath = "";
            public long Length;
            public string Digest = "";
            public DateTime StoredAtUtc;
            public DateTime ValidUntilUtc;
            public DateTime LastAccessUtc;
            public bool ValidityExpires;
            public TimeSpan Ttl;
            public long? LengthClaim;
            public string? ShaClaim;
        }

        private MetaRecord? _TryReadMeta(string path)
        {
            try
            {
                if (!_fs.FileExists(path))
                {
                    return null;
                }
                using var stream = _fs.OpenRead(path);
                using var reader = new BinaryReader(stream, Encoding.UTF8);
                if (reader.ReadInt32() != MetaMagic || reader.ReadInt32() != MetaSchemaVersion)
                {
                    return null;
                }
                var record = new MetaRecord
                {
                    Key = reader.ReadString(),
                };
                record.Identity = new FileIdentity(
                    reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString());
                record.Generation = reader.ReadInt64();
                record.RelativeDataPath = reader.ReadString();
                record.Length = reader.ReadInt64();
                record.Digest = reader.ReadString();
                record.StoredAtUtc = new DateTime(reader.ReadInt64());
                record.ValidUntilUtc = new DateTime(reader.ReadInt64());
                record.LastAccessUtc = new DateTime(reader.ReadInt64());
                record.ValidityExpires = reader.ReadBoolean();
                record.Ttl = new TimeSpan(reader.ReadInt64());
                var lengthClaim = reader.ReadInt64();
                record.LengthClaim = lengthClaim >= 0 ? lengthClaim : null;
                record.ShaClaim = reader.ReadString();
                record.ShaClaim = record.ShaClaim.Length == 0 ? null : record.ShaClaim;
                if (record.Length < 0 || string.IsNullOrEmpty(record.Key))
                {
                    return null;
                }
                return record;
            }
            catch (Exception)
            {
                return null; // 结构损坏按恢复规则处理
            }
        }

        #endregion

        #region 小工具

        private static string _ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }

        private static async Task _DisposeQuietlyAsync(Stream stream)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 关闭失败由残留核对与清理路径处理
            }
        }

        private long _SafeFileLength(string path)
        {
            try
            {
                return _fs.FileExists(path) ? _fs.GetFileLength(path) : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private string[] _ListFilesSafe(string directory)
        {
            try
            {
                return _fs.GetFiles(directory);
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        private string[] _ListDirectoriesSafe(string directory)
        {
            try
            {
                return _fs.GetDirectories(directory);
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        private static Dictionary<string, object> _ContextOf(string key, FileRequest request, string? fact = null)
        {
            var uri = request.Source;
            var context = new Dictionary<string, object>
            {
                { "key", key },
                { "url", uri.Scheme + "://" + uri.Host + uri.AbsolutePath }, // 日志脱敏
                { "identity", request.Identity.ToString() },
            };
            if (fact != null)
            {
                context["fact"] = fact;
            }
            return context;
        }

        private void _TryDeleteFile(string path)
        {
            try
            {
                _fs.DeleteFile(path);
            }
            catch (Exception)
            {
                // 启动清理失败：文件按 Garbage 在下一次维护中重试
            }
        }

        #endregion
    }
}
