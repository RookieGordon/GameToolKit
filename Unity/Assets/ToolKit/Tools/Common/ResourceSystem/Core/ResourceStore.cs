/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 共享资源仓库 (P1/P2, §5-§6、§9.3)。以 ResourceKey (LoaderId+LocalKey) 管理共享加载：
 *                显式共享操作 + 独立等待者 + 引用持有 + 空闲 LRU 缓存 + 底层释放屏障。
 *                所有状态只在执行上下文内改变；状态提交段不 await 外部操作；
 *                先登记持有再完成等待者任务；取消事件只投递，不直接改字典。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common.Resource
{
    /// <summary> 一次 JoinOrAcquire 的结果：要么交付任务 (object = 具体 ResourceRef&lt;T&gt;)，要么等待屏障后重试 </summary>
    internal readonly struct AcquireOutcome
    {
        public readonly Task<object>? Deliver;
        public readonly Task? Barrier;

        private AcquireOutcome(Task<object>? deliver, Task? barrier)
        {
            Deliver = deliver;
            Barrier = barrier;
        }

        public static AcquireOutcome FromDeliver(Task<object> task) => new AcquireOutcome(task, null);

        public static AcquireOutcome FromDeliverNow(object reference) =>
            new AcquireOutcome(Task.FromResult(reference), null);

        public static AcquireOutcome FromBarrier(Task terminal) => new AcquireOutcome(null, terminal);
    }

    /// <summary> 单次调用者的等待者描述：目标类型、进度监听与凭证工厂 </summary>
    internal sealed class WaiterSpec
    {
        public readonly Type RequestedType;
        public readonly IProgress<ResourceProgress>? Progress;
        public readonly Func<ResourceEntry, object> RefFactory;

        public WaiterSpec(Type requestedType, IProgress<ResourceProgress>? progress, Func<ResourceEntry, object> refFactory)
        {
            RequestedType = requestedType;
            Progress = progress;
            RefFactory = refFactory;
        }
    }

    /// <summary> 共享加载等待者：独立状态、独立取消；只允许转换一次 </summary>
    internal sealed class LoadWaiter
    {
        public readonly long Id;
        public WaiterState State;
        public readonly WaiterSpec Spec;
        public readonly TaskCompletionSource<object> Completion;
        public readonly CancellationToken CallerToken;
        public CancellationTokenRegistration Registration;

        public LoadWaiter(long id, WaiterSpec spec, CancellationToken callerToken)
        {
            Id = id;
            Spec = spec;
            CallerToken = callerToken;
            Completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary> 资源条目：一个 key 对应一次底层加载所有权；持有与卸载屏障都挂在条目上 </summary>
    internal sealed class ResourceEntry
    {
        public readonly ResourceKey Key;
        public readonly LoaderRegistration Registration;
        public readonly ResolvedResource Resolved;
        public readonly string OperationId;
        public readonly MemoryPolicy Policy;
        public ResourceState State;
        public LoadedAsset? Asset;
        public bool AssetInvalid;
        public int HoldCount;
        public readonly List<LoadWaiter> Waiters = new List<LoadWaiter>();
        public CancellationTokenSource? OperationCts;
        public readonly TaskCompletionSource<object> Terminal =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        public double IdleSince;
        public long EstimatedBytes;
        public LoadError? StoredCleanupError;
        public LinkedListNode<ResourceEntry>? IdleNode;

        public ResourceEntry(ResourceKey key, LoaderRegistration registration, ResolvedResource resolved,
            string operationId, MemoryPolicy policy)
        {
            Key = key;
            Registration = registration;
            Resolved = resolved;
            OperationId = operationId;
            Policy = policy;
            State = ResourceState.Loading;
        }

        public CancellationToken OperationToken => OperationCts?.Token ?? CancellationToken.None;

        /// <summary> 条目终局屏障任务：条目安全移除 (或清理故障) 后完成 </summary>
        public Task TerminalTask => Terminal.Task;
    }

    internal sealed class ResourceStore
    {
        private readonly IExecutionContext _context;
        private readonly IResourceDiagnostics _diagnostics;
        private readonly Func<double> _monotonicNow;
        private readonly MemoryPolicy _defaultMemory;
        private readonly Func<bool> _isAcceptingNewRequests;
        private readonly Func<bool> _isCachingAllowed;
        private readonly Action _checkShutdownComplete;

        private readonly Dictionary<ResourceKey, ResourceEntry> _entries = new Dictionary<ResourceKey, ResourceEntry>();
        private readonly LinkedList<ResourceEntry> _idleLru = new LinkedList<ResourceEntry>(); // 头 = 最新

        private long _waiterSeed;
        private long _leaseSeed;
        private long _operationSeed;

        internal ResourceStore(
            IExecutionContext context,
            IResourceDiagnostics diagnostics,
            Func<double> monotonicNow,
            MemoryPolicy defaultMemory,
            Func<bool> isAcceptingNewRequests,
            Func<bool> isCachingAllowed,
            Action checkShutdownComplete)
        {
            _context = context;
            _diagnostics = diagnostics;
            _monotonicNow = monotonicNow;
            _defaultMemory = defaultMemory;
            _isAcceptingNewRequests = isAcceptingNewRequests;
            _isCachingAllowed = isCachingAllowed;
            _checkShutdownComplete = checkShutdownComplete;
        }

        internal IExecutionContext Context => _context;

        #region 命中与加入共享操作 (§6.3)

        /// <summary>
        /// 全程在上下文内。可能结果：立即/等待交付，或 Draining/Unloading/失效清理屏障 (等待后由调用者重新解析)。
        /// ReleaseFailed 条目直接抛出存储的清理故障。
        /// </summary>
        internal AcquireOutcome JoinOrAcquire(
            ResourceKey key,
            LoaderRegistration registration,
            ResolvedResource resolved,
            WaiterSpec spec,
            CancellationToken callerCt)
        {
            if (!_isAcceptingNewRequests())
            {
                throw new ResourceLoadException(_ManagerClosing());
            }
            callerCt.ThrowIfCancellationRequested();

            if (_entries.TryGetValue(key, out var e))
            {
                switch (e.State)
                {
                    case ResourceState.Draining:
                    case ResourceState.Unloading:
                        return AcquireOutcome.FromBarrier(e.TerminalTask);
                    case ResourceState.ReleaseFailed:
                        throw new ResourceLoadException(e.StoredCleanupError!);
                    case ResourceState.Ready:
                    case ResourceState.Idle:
                        return _AcquireReady(e, spec);
                    case ResourceState.Loading:
                        return AcquireOutcome.FromDeliver(_AddWaiter(e, spec, callerCt));
                }
            }

            var policy = registration.Policy?.Memory ?? _defaultMemory;
            e = new ResourceEntry(key, registration, resolved, "op-" + Interlocked.Increment(ref _operationSeed), policy)
            {
                OperationCts = new CancellationTokenSource()
            };
            _entries[key] = e;
            // 必须先登记首个等待者，再启动加载
            var task = _AddWaiter(e, spec, callerCt);
            _StartLoad(e);
            return AcquireOutcome.FromDeliver(task);
        }

        /// <summary>
        /// 以固定的 registration/resolved 快照取得原型引用 (实例池初始化用，§15.3.4)：
        /// 等待同键屏障后继续使用同一不可变版本描述，不重新解析。
        /// </summary>
        internal async Task<ResourceRef<object>> AcquireResolvedAsync(
            LoaderRegistration registration,
            ResolvedResource resolved,
            CancellationToken callerCt)
        {
            var spec = new WaiterSpec(typeof(object), null,
                entry => new ResourceRef<object>(this, entry, NewLeaseId()));
            while (true)
            {
                callerCt.ThrowIfCancellationRequested();
                var key = new ResourceKey(registration.Name, resolved.LocalKey);
                var outcome = _context.Invoke(() => JoinOrAcquire(key, registration, resolved, spec, callerCt));
                if (outcome.Barrier != null)
                {
                    await outcome.Barrier.WaitWithCancellation(callerCt).ConfigureAwait(false);
                    continue;
                }
                var boxed = await outcome.Deliver!.ConfigureAwait(false);
                return (ResourceRef<object>)boxed;
            }
        }

        internal long NewLeaseId()
        {
            return Interlocked.Increment(ref _leaseSeed);
        }

        private AcquireOutcome _AcquireReady(ResourceEntry e, WaiterSpec spec)
        {
            var asset = e.Asset!;
            if (e.AssetInvalid || !_SafeIsAlive(asset))
            {
                // 后端对象被外部销毁：旧引用访问失败，新请求等待清理屏障后重新加载
                e.AssetInvalid = true;
                _RemoveFromIdleLru(e);
                if (e.HoldCount == 0)
                {
                    _BeginUnload(e);
                }
                return AcquireOutcome.FromBarrier(e.TerminalTask);
            }
            if (!spec.RequestedType.IsInstanceOfType(asset.Value))
            {
                throw new ResourceLoadException(_TypeMismatch(e, spec.RequestedType, asset.Value));
            }
            if (e.State == ResourceState.Idle)
            {
                _RemoveFromIdleLru(e);
                e.State = ResourceState.Ready;
            }
            e.HoldCount++;
            var reference = spec.RefFactory(e);
            return AcquireOutcome.FromDeliverNow(reference);
        }

        private Task<object> _AddWaiter(ResourceEntry e, WaiterSpec spec, CancellationToken callerCt)
        {
            var w = new LoadWaiter(Interlocked.Increment(ref _waiterSeed), spec, callerCt);
            e.Waiters.Add(w);
            // 取消注册回调只投递事件；等待者终结后的注销由请求方 linked CTS 的释放完成
            w.Registration = callerCt.Register(() => _context.Post(() => _CancelWaiter(e, w)));
            return w.Completion.Task;
        }

        private void _CancelWaiter(ResourceEntry e, LoadWaiter w)
        {
            if (w.State != WaiterState.Pending)
            {
                return;
            }
            w.State = WaiterState.Cancelled;
            e.Waiters.Remove(w);
            w.Completion.TrySetCanceled();
            if (e.State == ResourceState.Loading && e.Waiters.Count == 0)
            {
                // 不删 entries[key]：新请求必须等这次操作结束 (同键屏障)
                e.State = ResourceState.Draining;
                e.OperationCts?.Cancel();
            }
        }

        private void _FailWaiter(ResourceEntry e, LoadWaiter w, LoadError error)
        {
            if (w.State != WaiterState.Pending)
            {
                return;
            }
            w.State = WaiterState.Failed;
            e.Waiters.Remove(w);
            w.Completion.TrySetException(new ResourceLoadException(error));
        }

        #endregion

        #region 加载执行与提交 (§6.4)

        private void _StartLoad(ResourceEntry e)
        {
            _ = _RunLoadAsync(e);
        }

        private async Task _RunLoadAsync(ResourceEntry e)
        {
            LoadedAsset? result = null;
            LoadError? failure = null;
            var cancelled = false;
            var slots = e.Registration.LoadSlots;
            var acquired = false;
            try
            {
                if (slots != null)
                {
                    await slots.WaitAsync(e.OperationToken).ConfigureAwait(false);
                    acquired = true;
                }
                result = await e.Registration.Loader.LoadAsync(e.Resolved, new FanOutProgress(this, e), e.OperationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (e.OperationToken.IsCancellationRequested)
            {
                // 正常取消：加载器契约保证已回退本次取得的资源
                cancelled = true;
            }
            catch (Exception ex)
            {
                // 加载器未返回即失败：ResourceLoadException 保留原错误；未分类异常归 internal.unexpected，
                // 清理状态无法确认时进入隔离屏障，不猜测已经清理
                failure = _ClassifyLoadFailure(ex, e);
            }
            finally
            {
                if (acquired)
                {
                    slots.Release();
                }
            }

            _context.Post(() =>
            {
                if (cancelled)
                {
                    _CompleteCancelledLoad(e);
                }
                else if (failure != null)
                {
                    _FailLoad(e, failure);
                }
                else
                {
                    // 正常返回 null 也必须提交，不能让条目停留在 Loading
                    _CompleteLoad(e, result);
                }
            });
        }

        private LoadError _ClassifyLoadFailure(Exception ex, ResourceEntry e)
        {
            if (ex is ResourceLoadException rle)
            {
                return rle.Error;
            }
            return new LoadError(
                DiagnosticCodes.InternalUnexpected,
                LoadStage.LoadAsset,
                CleanupStatus.Unknown,
                ex,
                _ContextOf(e));
        }

        private Dictionary<string, object> _ContextOf(ResourceEntry e)
        {
            return new Dictionary<string, object>
            {
                { "loaderId", e.Key.LoaderId },
                { "key", e.Key.ToString() },
                { "operationId", e.OperationId },
            };
        }

        private void _CompleteCancelledLoad(ResourceEntry e)
        {
            foreach (var w in e.Waiters.ToArray())
            {
                _CancelWaiter(e, w);
            }
            _RemoveEntry(e);
            e.Terminal.TrySetResult(null!);
            _DisposeOperationCts(e);
            _checkShutdownComplete();
        }

        private void _FailLoad(ResourceEntry e, LoadError error)
        {
            if (!_entries.TryGetValue(e.Key, out var current) || !ReferenceEquals(current, e))
            {
                _diagnostics.Report(new LoadError(
                    DiagnosticCodes.InternalConsistency, LoadStage.LoadAsset,
                    CleanupStatus.Unknown, null, _ContextOf(e)));
                return;
            }

            if (error.Cleanup != CleanupStatus.Complete)
            {
                // 清理结果不确定：保留同键故障屏障，不能复用或重新加载同键
                e.State = ResourceState.ReleaseFailed;
                e.StoredCleanupError = error;
                foreach (var w in e.Waiters.ToArray())
                {
                    if (w.CallerToken.IsCancellationRequested)
                    {
                        _CancelWaiter(e, w);
                    }
                    else
                    {
                        _FailWaiter(e, w, error);
                    }
                }
                e.Terminal.TrySetException(new ResourceLoadException(error));
            }
            else
            {
                foreach (var w in e.Waiters.ToArray())
                {
                    if (w.CallerToken.IsCancellationRequested)
                    {
                        _CancelWaiter(e, w);
                    }
                    else
                    {
                        _FailWaiter(e, w, error);
                    }
                }
                _RemoveEntry(e);
                e.Terminal.TrySetResult(null!);
            }

            // 操作级诊断：一次最终失败，所有等待者共享同一 DiagnosticId
            _diagnostics.Report(error);
            _DisposeOperationCts(e);
            _checkShutdownComplete();
        }

        private void _CompleteLoad(ResourceEntry e, LoadedAsset? asset)
        {
            if (!_entries.TryGetValue(e.Key, out var current) || !ReferenceEquals(current, e))
            {
                if (asset != null)
                {
                    _ = _ReleaseAssetQuietly(asset); // 条目已不被字典认识，不能让转交结果泄漏
                }
                _DisposeOperationCts(e);
                return;
            }

            if (asset == null)
            {
                // 没有向核心转交任何可释放结果：失败等待者并移除条目，允许重试
                var error = new LoadError(DiagnosticCodes.LoaderInvalidResult, LoadStage.LoadAsset,
                    CleanupStatus.Complete, null, _ContextOf(e));
                foreach (var w in e.Waiters.ToArray())
                {
                    if (w.CallerToken.IsCancellationRequested)
                    {
                        _CancelWaiter(e, w);
                    }
                    else
                    {
                        _FailWaiter(e, w, error);
                    }
                }
                _RemoveEntry(e);
                e.Terminal.TrySetResult(null!);
                _diagnostics.Report(error);
                _DisposeOperationCts(e);
                _checkShutdownComplete();
                return;
            }

            e.Asset = asset;
            e.EstimatedBytes = asset.EstimatedBytes ?? 0;

            if (asset.Value == null || !_SafeIsAlive(asset))
            {
                var error = new LoadError(DiagnosticCodes.LoaderInvalidResult, LoadStage.LoadAsset,
                    CleanupStatus.Complete, null, _ContextOf(e));
                foreach (var w in e.Waiters.ToArray())
                {
                    if (w.CallerToken.IsCancellationRequested)
                    {
                        _CancelWaiter(e, w);
                    }
                    else
                    {
                        _FailWaiter(e, w, error);
                    }
                }
                _BeginUnload(e); // 非空但无效的结果仍需走清理屏障
                _diagnostics.Report(error);
                _DisposeOperationCts(e);
                _checkShutdownComplete();
                return;
            }

            if (e.State == ResourceState.Draining || !_isAcceptingNewRequests())
            {
                // 排空或关闭中的迟到成功：不发放新引用，结果直接进入卸载
                foreach (var w in e.Waiters.ToArray())
                {
                    if (w.CallerToken.IsCancellationRequested)
                    {
                        _CancelWaiter(e, w);
                    }
                    else
                    {
                        _FailWaiter(e, w, _ManagerClosing());
                    }
                }
                _BeginUnload(e);
                _DisposeOperationCts(e);
                _checkShutdownComplete();
                return;
            }

            var deliveries = new List<KeyValuePair<LoadWaiter, object>>();
            foreach (var w in e.Waiters.ToArray())
            {
                if (w.State != WaiterState.Pending)
                {
                    continue;
                }
                if (w.CallerToken.IsCancellationRequested)
                {
                    _CancelWaiter(e, w);
                    continue;
                }
                if (!w.Spec.RequestedType.IsInstanceOfType(asset.Value))
                {
                    _FailWaiter(e, w, _TypeMismatch(e, w.Spec.RequestedType, asset.Value));
                    continue;
                }
                // 先登记持有，再交付 (等待者任务完成即视为持有成立)
                e.HoldCount++;
                w.State = WaiterState.Granted;
                e.Waiters.Remove(w);
                deliveries.Add(new KeyValuePair<LoadWaiter, object>(w, w.Spec.RefFactory(e)));
            }

            if (e.HoldCount > 0)
            {
                e.State = ResourceState.Ready;
            }
            else
            {
                // 无人接收的结果不进入缓存长期占用
                _BeginUnload(e);
            }

            foreach (var pair in deliveries)
            {
                pair.Key.Completion.TrySetResult(pair.Value);
            }

            _DisposeOperationCts(e);
            _checkShutdownComplete();
        }

        #endregion

        #region 持有、空闲与卸载 (§6.5、§9.3)

        internal ResourceRef<T> Retain<T>(ResourceEntry entry) where T : class
        {
            if (!_isAcceptingNewRequests())
            {
                throw new ResourceLoadException(_ManagerClosing());
            }
            if (entry.State != ResourceState.Ready || entry.AssetInvalid || !_SafeIsAlive(entry.Asset!))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetInvalidated, LoadStage.LoadAsset,
                    CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "key", entry.Key.ToString() } }));
            }
            entry.HoldCount++;
            return new ResourceRef<T>(this, entry, Interlocked.Increment(ref _leaseSeed));
        }

        internal void Release(ResourceEntry entry, long leaseId)
        {
            if (entry.HoldCount <= 0)
            {
                _diagnostics.Report(new LoadError(
                    DiagnosticCodes.InternalConsistency, LoadStage.ReleaseAsset,
                    CleanupStatus.Unknown, null,
                    new Dictionary<string, object>
                    {
                        { "key", entry.Key.ToString() },
                        { "leaseId", leaseId },
                    }));
                return;
            }
            entry.HoldCount--;
            if (entry.HoldCount != 0)
            {
                return;
            }

            var entryPolicy = entry.Policy;
            if (!_isCachingAllowed() || entry.AssetInvalid || entryPolicy.IdleLifetime <= TimeSpan.Zero
                || _defaultMemory.MaxIdleEntries <= 0)
            {
                _BeginUnload(entry);
                return;
            }

            entry.State = ResourceState.Idle;
            entry.IdleSince = _monotonicNow();
            entry.IdleNode = _idleLru.AddFirst(entry);
            _EnforceBudget();
        }

        private void _BeginUnload(ResourceEntry e)
        {
            if (e.HoldCount != 0)
            {
                _diagnostics.Report(new LoadError(
                    DiagnosticCodes.InternalConsistency, LoadStage.ReleaseAsset,
                    CleanupStatus.Unknown, null, _ContextOf(e)));
                return;
            }
            if (e.State == ResourceState.Unloading || e.State == ResourceState.ReleaseFailed
                || e.State == ResourceState.Removed)
            {
                return;
            }
            _RemoveFromIdleLru(e);
            e.State = ResourceState.Unloading;
            _ = _RunUnloadAsync(e);
        }

        private async Task _RunUnloadAsync(ResourceEntry e)
        {
            try
            {
                if (e.Asset != null)
                {
                    await e.Asset.ReleaseAsync().ConfigureAwait(false); // 至多启动一次
                }
                _context.Post(() =>
                {
                    if (_entries.TryGetValue(e.Key, out var current) && ReferenceEquals(current, e))
                    {
                        _entries.Remove(e.Key);
                    }
                    e.State = ResourceState.Removed;
                    e.Terminal.TrySetResult(null!);
                    _checkShutdownComplete();
                });
            }
            catch (Exception ex)
            {
                var error = new LoadError(
                    DiagnosticCodes.LifecycleReleaseFailed,
                    LoadStage.ReleaseAsset,
                    CleanupStatus.Incomplete,
                    ex,
                    _ContextOf(e));
                _context.Post(() =>
                {
                    e.State = ResourceState.ReleaseFailed;
                    e.StoredCleanupError = error;
                    e.Terminal.TrySetException(new ResourceLoadException(error));
                    _diagnostics.Report(error);
                    _checkShutdownComplete();
                });
            }
        }

        private async Task _ReleaseAssetQuietly(LoadedAsset asset)
        {
            try
            {
                await asset.ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _diagnostics.Report(new LoadError(
                    DiagnosticCodes.LifecycleReleaseFailed, LoadStage.ReleaseAsset,
                    CleanupStatus.Incomplete, ex));
            }
        }

        private void _DisposeOperationCts(ResourceEntry e)
        {
            e.OperationCts?.Dispose();
            e.OperationCts = null;
        }

        /// <summary> 仅当字典中该键仍是本条目时移除；同时脱离空闲 LRU </summary>
        private void _RemoveEntry(ResourceEntry e)
        {
            if (_entries.TryGetValue(e.Key, out var current) && ReferenceEquals(current, e))
            {
                _entries.Remove(e.Key);
            }
            _RemoveFromIdleLru(e);
        }

        #endregion

        #region 空闲缓存维护 (P2)

        private void _RemoveFromIdleLru(ResourceEntry e)
        {
            if (e.IdleNode != null)
            {
                _idleLru.Remove(e.IdleNode);
                e.IdleNode = null;
            }
        }

        private void _EnforceBudget()
        {
            if (_defaultMemory.MaxIdleEntries > 0)
            {
                while (_idleLru.Count > _defaultMemory.MaxIdleEntries)
                {
                    var oldest = _idleLru.Last!.Value;
                    _BeginUnload(oldest);
                }
            }
            if (_defaultMemory.MaxEstimatedIdleBytes is long cap)
            {
                var sum = 0L;
                for (var node = _idleLru.First; node != null; node = node.Next)
                {
                    sum += node.Value.EstimatedBytes;
                }
                while (sum > cap && _idleLru.Count > 0)
                {
                    var oldest = _idleLru.Last!.Value;
                    sum -= oldest.EstimatedBytes;
                    _BeginUnload(oldest);
                }
            }
        }

        internal void TickMaintenance()
        {
            var now = _monotonicNow();
            var node = _idleLru.Last;
            while (node != null)
            {
                var next = node.Previous;
                if (now - node.Value.IdleSince >= node.Value.Policy.IdleLifetime.TotalSeconds)
                {
                    _BeginUnload(node.Value);
                }
                node = next;
            }
            _EnforceBudget();
        }

        /// <summary> 立即卸载全部空闲条目；返回本批次的终局任务 </summary>
        internal List<Task> UnloadAllIdle()
        {
            var tasks = new List<Task>();
            var node = _idleLru.Last;
            while (node != null)
            {
                var next = node.Previous;
                tasks.Add(node.Value.TerminalTask);
                _BeginUnload(node.Value);
                node = next;
            }
            return tasks;
        }

        #endregion

        #region 关闭与查询

        internal void BeginClose()
        {
            foreach (var e in _entries.Values)
            {
                switch (e.State)
                {
                    case ResourceState.Loading:
                        e.State = ResourceState.Draining;
                        e.OperationCts?.Cancel();
                        break;
                    case ResourceState.Idle:
                        _BeginUnload(e);
                        break;
                    // Ready 继续为已交付引用服务；Draining/Unloading/ReleaseFailed 保持原路径
                }
            }
        }

        /// <summary> 没有仍在飞行 (Loading/Draining/Idle/Unloading) 的条目 </summary>
        internal bool IsQuiesced
        {
            get
            {
                foreach (var e in _entries.Values)
                {
                    if (_IsInFlight(e))
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        private static bool _IsInFlight(ResourceEntry e)
        {
            return e.State == ResourceState.Loading || e.State == ResourceState.Draining
                   || e.State == ResourceState.Idle || e.State == ResourceState.Unloading;
        }

        internal int EntryCount => _entries.Count;

        internal List<LoadError> CollectStuckErrors()
        {
            var list = new List<LoadError>();
            foreach (var e in _entries.Values)
            {
                if (e.State == ResourceState.ReleaseFailed && e.StoredCleanupError != null)
                {
                    list.Add(e.StoredCleanupError);
                }
            }
            return list;
        }

        internal List<string> DescribeOutstanding()
        {
            var list = new List<string>();
            foreach (var e in _entries.Values)
            {
                if (e.State == ResourceState.Ready && e.HoldCount > 0)
                {
                    list.Add($"{e.Key} state={e.State} holds={e.HoldCount}");
                }
            }
            return list;
        }

        internal List<ResourceRow> SnapshotRows()
        {
            var list = new List<ResourceRow>(_entries.Count);
            foreach (var e in _entries.Values)
            {
                list.Add(_ToRow(e));
            }
            return list;
        }

        private ResourceRow _ToRow(ResourceEntry e)
        {
            return new ResourceRow(e.Key, e.State, e.HoldCount, e.Waiters.Count, e.EstimatedBytes);
        }

        #endregion

        #region 凭证访问 (供 ResourceRef 调用，要求上下文)

        internal object GetLiveAsset(ResourceEntry entry, long leaseId)
        {
            if (entry.State != ResourceState.Ready && entry.State != ResourceState.Idle
                || entry.AssetInvalid || entry.Asset == null || !_SafeIsAlive(entry.Asset))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetInvalidated, LoadStage.LoadAsset,
                    CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "key", entry.Key.ToString() },
                        { "leaseId", leaseId },
                    }));
            }
            return entry.Asset.Value;
        }

        internal bool IsEntryLive(ResourceEntry entry)
        {
            return (entry.State == ResourceState.Ready || entry.State == ResourceState.Idle)
                   && !entry.AssetInvalid
                   && entry.Asset != null
                   && _SafeIsAlive(entry.Asset);
        }

        private static bool _SafeIsAlive(LoadedAsset asset)
        {
            try
            {
                return asset.IsAlive();
            }
            catch
            {
                return false;
            }
        }

        #endregion

        private static LoadError _ManagerClosing()
        {
            return new LoadError(DiagnosticCodes.LifecycleManagerClosing, LoadStage.Shutdown,
                CleanupStatus.Complete);
        }

        private static LoadError _TypeMismatch(ResourceEntry e, Type requested, object actual)
        {
            return new LoadError(
                DiagnosticCodes.AssetTypeMismatch, LoadStage.LoadAsset, CleanupStatus.Complete, null,
                new Dictionary<string, object>
                {
                    { "key", e.Key.ToString() },
                    { "requestedType", requested.Name },
                    { "actualType", actual.GetType().Name },
                });
        }

        /// <summary>
        /// 进度扇出：在上下文内快照等待者监听器，在状态提交之外执行回调；
        /// 监听器抛异常不得让加载失败 (归类为观察者故障)。
        /// </summary>
        private sealed class FanOutProgress : IProgress<ResourceProgress>
        {
            private readonly ResourceStore _store;
            private readonly ResourceEntry _entry;

            public FanOutProgress(ResourceStore store, ResourceEntry entry)
            {
                _store = store;
                _entry = entry;
            }

            public void Report(ResourceProgress value)
            {
                IProgress<ResourceProgress>?[] targets;
                try
                {
                    targets = _store._context.Invoke(() => _Snapshot());
                }
                catch
                {
                    return;
                }
                foreach (var target in targets)
                {
                    if (target == null)
                    {
                        continue;
                    }
                    try
                    {
                        target.Report(value);
                    }
                    catch (Exception ex)
                    {
                        _store._diagnostics.Report(new LoadError(
                            DiagnosticCodes.ObserverCallbackFailed, value.Stage,
                            CleanupStatus.Complete, ex));
                    }
                }
            }

            private IProgress<ResourceProgress>?[] _Snapshot()
            {
                var list = new IProgress<ResourceProgress>?[_entry.Waiters.Count];
                for (var i = 0; i < _entry.Waiters.Count; i++)
                {
                    list[i] = _entry.Waiters[i].Spec.Progress;
                }
                return list;
            }
        }
    }
}
