/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 共享资源及其引用。阅读顺序：获取引用 → 加载完成 → 归还/卸载 → 空闲维护。
 *                同键请求复用一次加载，每个调用者独立取得引用。
 *                旧条目必须完成清理才能重新加载；引用归零后才允许缓存或卸载。
 *                状态判断和持有登记在执行上下文内完成，异步等待在外部进行。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
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

        #region 获取引用：解析 → 复用或等待加载 → 返回独立引用

        /// <summary>
        /// 按业务地址获取引用。旧条目退出后重新解析地址，允许加载器映射到新的资源版本。
        /// </summary>
        internal Task<ResourceRef<T>> LoadReferenceAsync<T>(
            LoaderRegistration registration,
            ResourceRequest request,
            IProgress<ResourceProgress>? progress,
            CancellationToken callerToken) where T : class
        {
            return _AcquireReferenceAsync<T>(
                registration, token => registration.Loader.ResolveAsync(request, token),
                request.Address, progress, callerToken);
        }

        /// <summary>
        /// 实例池已经选定原型版本；等待旧条目退出后仍使用同一份描述，不重新解析地址。
        /// </summary>
        internal Task<ResourceRef<object>> AcquireResolvedAsync(
            LoaderRegistration registration,
            ResolvedResource resolved,
            CancellationToken callerToken)
        {
            return _AcquireReferenceAsync<object>(
                registration, _ => Task.FromResult(resolved), resolved.LocalKey, null, callerToken);
        }

        /// <summary>
        /// 获取引用的完整流程。只在旧条目尚未退出时重试；加载失败直接交给调用者。
        /// 查找、登记等待和增加持有必须一起执行，不能在判断后让其他请求先卸载资源。
        /// </summary>
        private async Task<ResourceRef<T>> _AcquireReferenceAsync<T>(
            LoaderRegistration registration,
            Func<CancellationToken, Task<ResolvedResource>> resolve,
            string address,
            IProgress<ResourceProgress>? progress,
            CancellationToken callerToken) where T : class
        {
            while (true)
            {
                callerToken.ThrowIfCancellationRequested();
                var resolved = await resolve(callerToken).ConfigureAwait(false);
                ValidateResolved(resolved, address);
                var key = new ResourceKey(registration.Name, resolved.LocalKey);
                ResourceEntry entry = null!;
                Task referenceGranted = Task.CompletedTask;
                Task? previousRemoval = null;

                _context.Invoke(() =>
                {
                    if (!_isAcceptingNewRequests())
                    {
                        throw new ResourceLoadException(_ManagerClosing());
                    }
                    callerToken.ThrowIfCancellationRequested();

                    if (_entries.TryGetValue(key, out var existing))
                    {
                        entry = existing;
                        switch (entry.State)
                        {
                            case ResourceState.Ready:
                            case ResourceState.Idle:
                                if (!entry.IsAssetInvalid && _SafeIsAlive(entry.Asset!))
                                {
                                    _ReserveReference(entry, typeof(T));
                                    return;
                                }
                                // 外部销毁的资源也要等旧持有者归还、清理完成，才能重新加载。
                                entry.IsAssetInvalid = true;
                                _RemoveFromIdleLru(entry);
                                if (entry.HoldCount == 0)
                                {
                                    _BeginUnload(entry);
                                }
                                previousRemoval = entry.RemovalTask;
                                return;

                            case ResourceState.Loading:
                                referenceGranted = _RegisterWaitingRequest(entry, typeof(T), progress, callerToken);
                                return;

                            case ResourceState.Draining:
                            case ResourceState.Unloading:
                                previousRemoval = entry.RemovalTask;
                                return;

                            case ResourceState.ReleaseFailed:
                                throw new ResourceLoadException(entry.CleanupError!);

                            default:
                                throw new InvalidOperationException($"已移除的资源仍在仓库中: {key}");
                        }
                    }

                    var policy = registration.Policy?.Memory ?? _defaultMemory;
                    entry = new ResourceEntry(key, registration, resolved,
                        "op-" + Interlocked.Increment(ref _operationSeed), policy)
                    {
                        LoadCancellation = new CancellationTokenSource()
                    };
                    _entries.Add(key, entry);
                    // 先登记请求，加载器即使同步完成也不会丢失首个接收者。
                    referenceGranted = _RegisterWaitingRequest(entry, typeof(T), progress, callerToken);
                    _ = _RunLoadAsync(entry);
                });

                if (previousRemoval != null)
                {
                    await previousRemoval.WaitWithCancellation(callerToken).ConfigureAwait(false);
                    continue;
                }

                // 等待者已在上下文内完成取消/授权裁决并登记持有，此处不能再次取消而丢掉持有。
                await referenceGranted.ConfigureAwait(false);
                return new ResourceRef<T>(this, entry, _NextReferenceId());
            }
        }

        private long _NextReferenceId()
        {
            return Interlocked.Increment(ref _leaseSeed);
        }

        private void _ReserveReference(ResourceEntry entry, Type requestedType)
        {
            var asset = entry.Asset!;
            if (!requestedType.IsInstanceOfType(asset.Value))
            {
                throw new ResourceLoadException(_TypeMismatch(entry, requestedType, asset.Value));
            }
            if (entry.State == ResourceState.Idle)
            {
                _RemoveFromIdleLru(entry);
                entry.State = ResourceState.Ready;
            }
            entry.HoldCount++;
        }

        private Task _RegisterWaitingRequest(
            ResourceEntry entry, Type requestedType, IProgress<ResourceProgress>? progress,
            CancellationToken callerToken)
        {
            var waiter = new PendingResourceRequest(requestedType, progress, callerToken);
            entry.PendingRequests.Add(waiter);
            // 请求结束后，管理器/实例池释放所属 CTS；回调只投递取消，不直接改变条目。
            callerToken.Register(() => _context.Post(() => _CancelRequest(entry, waiter)));
            return waiter.ReferenceGranted.Task;
        }

        private void _CancelRequest(ResourceEntry entry, PendingResourceRequest waiter)
        {
            if (waiter.State != WaiterState.Pending)
            {
                return;
            }
            waiter.State = WaiterState.Cancelled;
            entry.PendingRequests.Remove(waiter);
            waiter.ReferenceGranted.TrySetCanceled();
            if (entry.State == ResourceState.Loading && entry.PendingRequests.Count == 0)
            {
                // 保留旧条目，等这次加载及其清理结束后才允许同键重新加载。
                entry.State = ResourceState.Draining;
                entry.LoadCancellation?.Cancel();
            }
        }

        private void _FailRequest(ResourceEntry entry, PendingResourceRequest waiter, LoadError error)
        {
            if (waiter.State != WaiterState.Pending)
            {
                return;
            }
            waiter.State = WaiterState.Failed;
            entry.PendingRequests.Remove(waiter);
            waiter.ReferenceGranted.TrySetException(new ResourceLoadException(error));
        }

        #endregion

        #region 加载执行与提交 (§6.4)

        private async Task _RunLoadAsync(ResourceEntry entry)
        {
            LoadedAsset? result = null;
            LoadError? failure = null;
            var cancelled = false;
            var slots = entry.Registration.LoadSlots;
            var acquired = false;
            try
            {
                if (slots != null)
                {
                    await slots.WaitAsync(entry.LoadToken).ConfigureAwait(false);
                    acquired = true;
                }
                result = await entry.Registration.Loader.LoadAsync(entry.Resolved, new LoadProgressReporter(this, entry), entry.LoadToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (entry.LoadToken.IsCancellationRequested)
            {
                // 正常取消：加载器契约保证已回退本次取得的资源
                cancelled = true;
            }
            catch (Exception ex)
            {
                // 加载器未返回即失败：ResourceLoadException 保留原错误；未分类异常归 internal.unexpected，
                // 清理状态无法确认时进入隔离屏障，不猜测已经清理
                failure = _ClassifyLoadFailure(ex, entry);
            }
            finally
            {
                if (acquired)
                {
                    slots!.Release();
                }
            }

            _context.Post(() =>
            {
                // 网络/后端操作已经结束；它的取消源无需跟随资源驻留到卸载。
                _DisposeLoadCancellation(entry);
                if (cancelled)
                {
                    _FinishCancelledLoad(entry);
                }
                else if (failure != null)
                {
                    _HandleLoadFailure(entry, failure);
                }
                else
                {
                    // 正常返回 null 也必须提交，不能让条目停留在 Loading
                    _HandleLoadedAsset(entry, result);
                }
                _checkShutdownComplete();
            });
        }

        private LoadError _ClassifyLoadFailure(Exception ex, ResourceEntry entry)
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
                _ContextOf(entry));
        }

        private Dictionary<string, object> _ContextOf(ResourceEntry entry)
        {
            return new Dictionary<string, object>
            {
                { "loaderId", entry.Key.LoaderId },
                { "key", entry.Key.ToString() },
                { "operationId", entry.OperationId },
            };
        }

        private void _FinishCancelledLoad(ResourceEntry entry)
        {
            foreach (var waiter in entry.PendingRequests.ToArray())
            {
                _CancelRequest(entry, waiter);
            }
            _CompleteRemoval(entry);
        }

        private void _HandleLoadFailure(ResourceEntry entry, LoadError error)
        {
            if (!_entries.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
            {
                _diagnostics.Report(new LoadError(
                    DiagnosticCodes.InternalConsistency, LoadStage.LoadAsset,
                    CleanupStatus.Unknown, null, _ContextOf(entry)));
                return;
            }

            _FailPendingRequests(entry, error);
            if (error.Cleanup == CleanupStatus.Complete)
            {
                _CompleteRemoval(entry);
            }
            else
            {
                // 后端没有确认清理完成，保留故障条目，禁止同键重新加载。
                _RecordCleanupFailure(entry, error);
            }

            // 操作级诊断：一次最终失败，所有等待者共享同一 DiagnosticId
            _diagnostics.Report(error);
        }

        private void _HandleLoadedAsset(ResourceEntry entry, LoadedAsset? asset)
        {
            if (!_entries.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
            {
                if (asset != null)
                {
                    _ = _ReleaseAssetQuietly(asset); // 条目已不被字典认识，不能让转交结果泄漏
                }
                return;
            }

            if (asset == null)
            {
                // 没有向核心转交任何可释放结果：失败等待者并移除条目，允许重试
                var error = new LoadError(DiagnosticCodes.LoaderInvalidResult, LoadStage.LoadAsset,
                    CleanupStatus.Complete, null, _ContextOf(entry));
                _FailPendingRequests(entry, error);
                _CompleteRemoval(entry);
                _diagnostics.Report(error);
                return;
            }

            entry.Asset = asset;
            entry.EstimatedBytes = asset.EstimatedBytes ?? 0;

            if (asset.Value == null || !_SafeIsAlive(asset))
            {
                var error = new LoadError(DiagnosticCodes.LoaderInvalidResult, LoadStage.LoadAsset,
                    CleanupStatus.Complete, null, _ContextOf(entry));
                _FailPendingRequests(entry, error);
                _BeginUnload(entry); // 非空但无效的结果仍需走清理屏障
                _diagnostics.Report(error);
                return;
            }

            if (entry.State == ResourceState.Draining || !_isAcceptingNewRequests())
            {
                // 排空或关闭中的迟到成功：不发放新引用，结果直接进入卸载
                _FailPendingRequests(entry, _ManagerClosing());
                _BeginUnload(entry);
                return;
            }

            _GrantReferences(entry);
        }

        /// <summary> 加载结果不能交付时，每个请求仍优先保留自己的取消结果。 </summary>
        private void _FailPendingRequests(ResourceEntry entry, LoadError error)
        {
            foreach (var waiter in entry.PendingRequests.ToArray())
            {
                if (waiter.CallerToken.IsCancellationRequested)
                {
                    _CancelRequest(entry, waiter);
                }
                else
                {
                    _FailRequest(entry, waiter, error);
                }
            }
        }

        /// <summary> 一次提交：先确认接收者并登记全部持有，再通知调用者领取引用。 </summary>
        private void _GrantReferences(ResourceEntry entry)
        {
            var grantedRequests = new List<PendingResourceRequest>();
            var asset = entry.Asset!;
            foreach (var waiter in entry.PendingRequests.ToArray())
            {
                if (waiter.State != WaiterState.Pending)
                {
                    continue;
                }
                if (waiter.CallerToken.IsCancellationRequested)
                {
                    _CancelRequest(entry, waiter);
                    continue;
                }
                if (!waiter.RequestedType.IsInstanceOfType(asset.Value))
                {
                    _FailRequest(entry, waiter, _TypeMismatch(entry, waiter.RequestedType, asset.Value));
                    continue;
                }
                // 先登记持有，再交付 (等待者任务完成即视为持有成立)
                entry.HoldCount++;
                waiter.State = WaiterState.Granted;
                entry.PendingRequests.Remove(waiter);
                grantedRequests.Add(waiter);
            }

            if (entry.HoldCount > 0)
            {
                entry.State = ResourceState.Ready;
            }
            else
            {
                // 无人接收的结果不进入缓存长期占用
                _BeginUnload(entry);
            }

            foreach (var waiter in grantedRequests)
            {
                waiter.ReferenceGranted.TrySetResult(true);
            }
        }

        #endregion

        #region 持有、空闲与卸载 (§6.5、§9.3)

        internal ResourceRef<T> Retain<T>(ResourceEntry entry) where T : class
        {
            if (!_isAcceptingNewRequests())
            {
                throw new ResourceLoadException(_ManagerClosing());
            }
            if (entry.State != ResourceState.Ready || entry.IsAssetInvalid || !_SafeIsAlive(entry.Asset!))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetInvalidated, LoadStage.LoadAsset,
                    CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "key", entry.Key.ToString() } }));
            }
            entry.HoldCount++;
            return new ResourceRef<T>(this, entry, _NextReferenceId());
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
            // 加载器独立内存策略 (R25)：MaxIdleEntries=0 表示该加载器不保留空闲资源；
            // TTL 作用域为条目策略，条目上限/字节预算按系统默认策略执行
            if (!_isCachingAllowed() || entry.IsAssetInvalid || entryPolicy.IdleLifetime <= TimeSpan.Zero
                || entryPolicy.MaxIdleEntries <= 0 || _defaultMemory.MaxIdleEntries <= 0)
            {
                _BeginUnload(entry);
                return;
            }

            entry.State = ResourceState.Idle;
            entry.IdleSince = _monotonicNow();
            entry.IdleNode = _idleLru.AddFirst(entry);
            _EnforceBudget();
        }

        private void _BeginUnload(ResourceEntry entry)
        {
            if (entry.HoldCount != 0)
            {
                _diagnostics.Report(new LoadError(
                    DiagnosticCodes.InternalConsistency, LoadStage.ReleaseAsset,
                    CleanupStatus.Unknown, null, _ContextOf(entry)));
                return;
            }
            if (entry.State == ResourceState.Unloading || entry.State == ResourceState.ReleaseFailed
                || entry.State == ResourceState.Removed)
            {
                return;
            }
            _RemoveFromIdleLru(entry);
            entry.State = ResourceState.Unloading;
            _ = _RunUnloadAsync(entry);
        }

        private async Task _RunUnloadAsync(ResourceEntry entry)
        {
            try
            {
                if (entry.Asset != null)
                {
                    await entry.Asset.ReleaseAsync().ConfigureAwait(false); // 至多启动一次
                }
                _context.Post(() =>
                {
                    _CompleteRemoval(entry);
                });
            }
            catch (Exception ex)
            {
                var error = new LoadError(
                    DiagnosticCodes.LifecycleReleaseFailed,
                    LoadStage.ReleaseAsset,
                    CleanupStatus.Incomplete,
                    ex,
                    _ContextOf(entry));
                _context.Post(() =>
                {
                    _RecordCleanupFailure(entry, error);
                    _diagnostics.Report(error);
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

        private void _DisposeLoadCancellation(ResourceEntry entry)
        {
            entry.LoadCancellation?.Dispose();
            entry.LoadCancellation = null;
        }

        /// <summary> 旧条目成功退出的唯一落点：先移除，再允许等待它的新请求重试。 </summary>
        private void _CompleteRemoval(ResourceEntry entry)
        {
            if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(entry.Key);
            }
            _RemoveFromIdleLru(entry);
            entry.State = ResourceState.Removed;
            entry.RemovalCompletion.TrySetResult(true);
            _checkShutdownComplete();
        }

        /// <summary> 清理未完成时留下故障条目；新请求得到错误，不能开始另一轮加载。 </summary>
        private void _RecordCleanupFailure(ResourceEntry entry, LoadError error)
        {
            entry.State = ResourceState.ReleaseFailed;
            entry.CleanupError = error;
            entry.RemovalCompletion.TrySetException(new ResourceLoadException(error));
            _checkShutdownComplete();
        }

        #endregion

        #region 空闲缓存维护 (P2)

        private void _RemoveFromIdleLru(ResourceEntry entry)
        {
            if (entry.IdleNode != null)
            {
                _idleLru.Remove(entry.IdleNode);
                entry.IdleNode = null;
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
                tasks.Add(node.Value.RemovalTask);
                _BeginUnload(node.Value);
                node = next;
            }
            return tasks;
        }

        #endregion

        #region 关闭与查询

        internal void BeginClose()
        {
            // 内联上下文中，启动卸载可能同步移除条目。
            foreach (var entry in new List<ResourceEntry>(_entries.Values))
            {
                switch (entry.State)
                {
                    case ResourceState.Loading:
                        entry.State = ResourceState.Draining;
                        entry.LoadCancellation?.Cancel();
                        break;
                    case ResourceState.Idle:
                        _BeginUnload(entry);
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
                foreach (var entry in _entries.Values)
                {
                    if (_IsInFlight(entry))
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        private static bool _IsInFlight(ResourceEntry entry)
        {
            return entry.State == ResourceState.Loading || entry.State == ResourceState.Draining
                   || entry.State == ResourceState.Idle || entry.State == ResourceState.Unloading;
        }

        internal int EntryCount => _entries.Count;

        internal List<LoadError> CollectStuckErrors()
        {
            var list = new List<LoadError>();
            foreach (var entry in _entries.Values)
            {
                if (entry.State == ResourceState.ReleaseFailed && entry.CleanupError != null)
                {
                    list.Add(entry.CleanupError);
                }
            }
            return list;
        }

        internal List<string> DescribeOutstanding()
        {
            var list = new List<string>();
            foreach (var entry in _entries.Values)
            {
                if (entry.State == ResourceState.Ready && entry.HoldCount > 0)
                {
                    list.Add($"{entry.Key} state={entry.State} holds={entry.HoldCount}");
                }
            }
            return list;
        }

        internal List<ResourceRow> SnapshotRows()
        {
            var list = new List<ResourceRow>(_entries.Count);
            foreach (var entry in _entries.Values)
            {
                list.Add(_ToRow(entry));
            }
            return list;
        }

        private ResourceRow _ToRow(ResourceEntry entry)
        {
            return new ResourceRow(entry.Key, entry.State, entry.HoldCount, entry.PendingRequests.Count, entry.EstimatedBytes);
        }

        #endregion

        internal static void ValidateResolved(ResolvedResource resolved, string address)
        {
            if (resolved == null || string.IsNullOrEmpty(resolved.LocalKey))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "address", address } }));
            }
            if (resolved.RepresentationType == null)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "address", address },
                        { "reason", "RepresentationType 不能为空" },
                    }));
            }
        }

        #region 凭证访问 (供 ResourceRef 调用，要求上下文)

        internal object GetLiveAsset(ResourceEntry entry, long leaseId)
        {
            if (entry.State != ResourceState.Ready && entry.State != ResourceState.Idle
                || entry.IsAssetInvalid || entry.Asset == null || !_SafeIsAlive(entry.Asset))
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
                   && !entry.IsAssetInvalid
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

        private static LoadError _TypeMismatch(ResourceEntry entry, Type requested, object actual)
        {
            return new LoadError(
                DiagnosticCodes.AssetTypeMismatch, LoadStage.LoadAsset, CleanupStatus.Complete, null,
                new Dictionary<string, object>
                {
                    { "key", entry.Key.ToString() },
                    { "requestedType", requested.Name },
                    { "actualType", actual.GetType().Name },
                });
        }

        /// <summary>
        /// 进度扇出：在上下文内快照等待者监听器，在状态提交之外执行回调；
        /// 监听器抛异常不得让加载失败 (归类为观察者故障)。
        /// </summary>
        private sealed class LoadProgressReporter : IProgress<ResourceProgress>
        {
            private readonly ResourceStore _store;
            private readonly ResourceEntry _entry;

            public LoadProgressReporter(ResourceStore store, ResourceEntry entry)
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
                var list = new IProgress<ResourceProgress>?[_entry.PendingRequests.Count];
                for (var i = 0; i < _entry.PendingRequests.Count; i++)
                {
                    list[i] = _entry.PendingRequests[i].Progress;
                }
                return list;
            }
        }
    }
    /// <summary> 尚未取得引用的单个请求；成功、取消或失败只发生一次。 </summary>
    internal sealed class PendingResourceRequest
    {
        public WaiterState State;
        public readonly Type RequestedType;
        public readonly IProgress<ResourceProgress>? Progress;
        // 只通知“持有已登记”，资源条目由获取流程持有，不在任务结果里重复传递。
        public readonly TaskCompletionSource<bool> ReferenceGranted;
        public readonly CancellationToken CallerToken;

        public PendingResourceRequest(Type requestedType, IProgress<ResourceProgress>? progress, CancellationToken callerToken)
        {
            RequestedType = requestedType;
            Progress = progress;
            CallerToken = callerToken;
            ReferenceGranted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary> 同一资源的一轮加载及后续持有。最后一次释放完成后，这个条目退出仓库。 </summary>
    internal sealed class ResourceEntry
    {
        public readonly ResourceKey Key;
        public readonly LoaderRegistration Registration;
        public readonly ResolvedResource Resolved;
        public readonly string OperationId;
        public readonly MemoryPolicy Policy;
        public ResourceState State;
        public LoadedAsset? Asset;
        public bool IsAssetInvalid;
        public int HoldCount;
        public readonly List<PendingResourceRequest> PendingRequests = new List<PendingResourceRequest>();
        public CancellationTokenSource? LoadCancellation;
        public readonly TaskCompletionSource<bool> RemovalCompletion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public double IdleSince;
        public long EstimatedBytes;
        public LoadError? CleanupError;
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

        public CancellationToken LoadToken => LoadCancellation?.Token ?? CancellationToken.None;

        /// <summary> 成功表示旧条目已移除，可重新加载；失败表示旧条目清理失败，禁止重试。 </summary>
        public Task RemovalTask => RemovalCompletion.Task;
    }
}
