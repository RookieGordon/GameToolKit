/*
 * author       : Gordon
 * datetime     : 2026/10/10
 * description  : 加载器路由与资源生命周期编排。
 *                路由/解析 → 复用或共享加载 → 交付引用 → 归还后缓存或卸载。
 *                本类保存临时加载上下文，ResourceStore 只保存实际产物与引用。
 *                状态在执行上下文中短暂提交，等待资源锁、加载和卸载均不阻塞线程。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    internal sealed class LoadManager
    {
        private readonly IExecutionContext _context;
        private readonly ResourceStore _store;
        private readonly Dictionary<ResourceKey, LoadContext> _loading = new Dictionary<ResourceKey, LoadContext>();
        // 没有产物但清理未确认完成的加载，只保留错误证据来阻止同键重试。
        private readonly Dictionary<ResourceKey, LoadError> _loadCleanupErrors = new Dictionary<ResourceKey, LoadError>();
        private readonly KeyedAsyncLock<ResourceKey> _resourceLocks = new KeyedAsyncLock<ResourceKey>();
        private readonly Dictionary<ResourceSource, LoaderRegistration> _loaders =
            new Dictionary<ResourceSource, LoaderRegistration>();
        private readonly IResourceDiagnostics _diagnostics;
        private readonly Func<double> _monotonicNow;
        private readonly MemoryPolicy _defaultMemory;
        private readonly Func<bool> _isDisposed;

        private long _leaseSeed;
        private long _operationSeed;

        internal LoadManager(
            IExecutionContext context,
            ResourceStore store,
            IResourceDiagnostics diagnostics,
            Func<double> monotonicNow,
            MemoryPolicy defaultMemory,
            Func<bool> isDisposed)
        {
            _context = context;
            _store = store;
            _diagnostics = diagnostics;
            _monotonicNow = monotonicNow;
            _defaultMemory = defaultMemory;
            _isDisposed = isDisposed;
        }

        internal IExecutionContext Context => _context;

        #region 加载器注册与路由

        internal void RegisterLoader(ResourceSource source, IResourceLoader loader, LoaderPolicy? policy)
        {
            if (_loaders.ContainsKey(source))
            {
                throw new InvalidOperationException($"资源来源已注册加载器: {source}；不能重复注册");
            }
            _loaders.Add(source, _CreateRegistration(source, loader, policy));
        }

        internal void ReplaceLoader(ResourceSource source, IResourceLoader loader, LoaderPolicy? policy)
        {
            if (!_loaders.TryGetValue(source, out var previous))
            {
                throw new InvalidOperationException($"资源来源尚未注册加载器: {source}，请先 RegisterLoader");
            }
            var replacement = _CreateRegistration(source, loader, policy);
            previous.LoadSlots?.Dispose();
            _loaders[source] = replacement;
        }

        private static LoaderRegistration _CreateRegistration(ResourceSource source, IResourceLoader loader, LoaderPolicy? policy)
        {
            var snapshot = policy != null ? LoaderPolicy.Clone(policy) : new LoaderPolicy();
            snapshot.Validate();
            return new LoaderRegistration(source, loader, snapshot);
        }

        internal LoaderRegistration Route(ResourceSource source)
        {
            ValidateSource(source);
            if (!_loaders.TryGetValue(source, out var registration))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderNotRegistered, LoadStage.Route, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "source", source.ToString() },
                        { "registeredSources", string.Join(",", _loaders.Keys) },
                    }));
            }
            return registration;
        }

        internal static void ValidateSource(ResourceSource source)
        {
            if (source < ResourceSource.Resources || source > ResourceSource.Remote)
                throw new ArgumentOutOfRangeException(nameof(source), source, "必须指定有效的资源来源");
        }

        /// <summary> 实例池先解析原型来确定池键，取得原型时继续使用同一份解析结果。 </summary>
        internal async Task<ResolvedResource> ResolveAsync(
            LoaderRegistration registration, ResourceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = await registration.Loader.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
            _ValidateResolved(resolved, request.Address);
            return resolved;
        }

        #endregion

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
        /// 解析身份 → 复用产物或登记等待 → 取得执行权并按需加载 → 领取本轮产物。
        /// 状态查询和登记在上下文中完成；加载锁等待及实际加载调用在提交段外执行。
        /// 循环只用于等待旧资源清理、或接续前一位远端请求的失败；本请求自己的加载失败直接抛出。
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
                // 1. 解析本次身份。普通 Load 在重试时重新解析，实例池传入的解析结果保持固定。
                callerToken.ThrowIfCancellationRequested();
                var resolved = await resolve(callerToken).ConfigureAwait(false);
                _ValidateResolved(resolved, address);
                var resKey = new ResourceKey(registration.Source, resolved.LocalKey);
                LoadContext load = null!;
                ResourceRef<T>? reference = null;
                Task? cleanupBeforeRetry = null;
                Task<KeyedAsyncLock<ResourceKey>.Releaser>? resourceLockTask = null;
                var waitingRequest = new PendingResourceRequest(progress, callerToken);

                // 2. 一次提交内完成：命中即持有；未命中即加入本轮等待并排队。
                // 产物发布也经过同一上下文，不能插入“查询未命中”和“登记等待”之间。
                await _context.RunAsync(() =>
                {
                    _CheckRequest(callerToken);
                    if (_loadCleanupErrors.TryGetValue(resKey, out var cleanupError))
                        throw new ResourceLoadException(cleanupError);

                    // 可用产物直接复用，不必等待资源锁。
                    if (_store.TryGetEntry(resKey, out var entry))
                    {
                        if (_store.IsLive(entry))
                        {
                            reference = _AcquireLoadedReference<T>(entry);
                            return;
                        }
                        cleanupBeforeRetry = _WaitForUnavailableResource(entry);
                        return;
                    }

                    // 未发布产物时，加入正在进行的加载；已放弃的操作须等实际清理结束。
                    if (_loading.TryGetValue(resKey, out load))
                    {
                        if (load.IsAbandoned)
                        {
                            cleanupBeforeRetry = load.Completion.Task;
                            return;
                        }
                    }
                    else
                    {
                        load = new LoadContext(resKey, resolved,
                            "op-" + Interlocked.Increment(ref _operationSeed),
                            registration.Policy.Memory ?? _defaultMemory);
                        _loading.Add(resKey, load);
                    }
                    // 入队只取得任务，不等待锁。登记后，本请求参与本轮失败通知和产物交付保护。
                    load.PendingRequests.Add(waitingRequest);
                    resourceLockTask = _resourceLocks.LockAsync(resKey, callerToken);
                }).ConfigureAwait(false);

                if (reference != null) return reference;
                if (cleanupBeforeRetry != null)
                {
                    await cleanupBeforeRetry.WaitWithCancellation(callerToken).ConfigureAwait(false);
                    continue;
                }

                KeyedAsyncLock<ResourceKey>.Releaser? resourceLock = null;
                var startedByThisRequest = false;
                try
                {
                    // 3. 等待取得资源锁，然后登记本请求是否负责启动本轮加载。
                    resourceLock = await resourceLockTask!.ConfigureAwait(false);
                    await _context.RunAsync(() =>
                    {
                        _CheckRequest(callerToken);
                        if (!load.IsAbandoned && !load.HasStarted)
                        {
                            // 先登记启动责任，取消后仍由本轮操作负责收尾。
                            load.HasStarted = true;
                            startedByThisRequest = true;
                        }
                    }).ConfigureAwait(false);

                    if (startedByThisRequest)
                    {
                        // 登记后即承担执行和收尾责任；即使调用者此时取消，也必须进入本轮操作。
                        // 后端调用位于状态提交之外；具体线程要求由加载器处理，不强制 Task.Run。
                        var operationLock = resourceLock!;
                        resourceLock = null;
                        var loadTask = _RunLoadAsync(load, registration, operationLock);
                        await loadTask.WaitWithCancellation(callerToken).ConfigureAwait(false);
                    }

                    // 4. 领取前复查本轮错误和具体产物身份，不能按旧查询结果领取下一轮产物。
                    await _context.RunAsync(() =>
                    {
                        _CheckRequest(callerToken);
                        if (load.Error != null)
                        {
                            if (startedByThisRequest || registration.Policy.FailurePolicy == LoadFailurePolicy.FailWaitingRequests
                                || load.Error.Cleanup != CleanupStatus.Complete)
                                throw new ResourceLoadException(load.Error);
                            // 前一位远端请求已经失败；本请求退出旧记录，重新解析后发起自己的尝试。
                            return;
                        }
                        // 只查询本轮交付保护的产物，不能让旧等待者领取下一轮的结果。
                        if (_store.TryGetEntry(resKey, out var entry) && ReferenceEquals(entry, load.DeliveryResource))
                        {
                            if (_store.IsLive(entry))
                            {
                                reference = _AcquireLoadedReference<T>(entry);
                                return;
                            }
                            cleanupBeforeRetry = _WaitForUnavailableResource(entry);
                            return;
                        }
                        cleanupBeforeRetry = load.Completion.Task;
                    }).ConfigureAwait(false);
                }
                catch (Exception) when (callerToken.IsCancellationRequested)
                {
                    // 本地批次失败与个人取消同时发生时，保留这个请求自己的取消结果。
                    throw new OperationCanceledException(callerToken);
                }
                finally
                {
                    // 未交给加载操作的锁由本请求归还；离开本轮等待时解除自己的交付保护。
                    resourceLock?.Dispose();
                    await _context.RunAsync(() => _RemoveWaitingRequest(load, waitingRequest)).ConfigureAwait(false);
                }

                if (reference != null) return reference;
                if (cleanupBeforeRetry != null)
                    await cleanupBeforeRetry.WaitWithCancellation(callerToken).ConfigureAwait(false);
            }
        }

        private void _CheckRequest(CancellationToken callerToken)
        {
            callerToken.ThrowIfCancellationRequested();
            if (_isDisposed()) throw new ResourceLoadException(_ManagerDisposed());
        }

        private long _NextReferenceId() => Interlocked.Increment(ref _leaseSeed);

        private Task _WaitForUnavailableResource(ResourceEntry entry)
        {
            if (entry.State == ResourceState.ReleaseFailed)
                throw new ResourceLoadException(entry.CleanupError!);
            if (entry.HoldCount == 0) _ReleaseUnusedResource(entry);
            return entry.RemovalTask;
        }

        private ResourceRef<T> _AcquireLoadedReference<T>(ResourceEntry entry) where T : class
        {
            var asset = entry.Asset;
            if (!typeof(T).IsInstanceOfType(asset.Value))
                throw new ResourceLoadException(_TypeMismatch(entry, typeof(T), asset.Value));
            _store.AddReference(entry);
            if (_loading.TryGetValue(entry.Key, out var load) && ReferenceEquals(load.DeliveryResource, entry))
                load.DeliveredToCaller = true;
            return new ResourceRef<T>(this, entry, _NextReferenceId());
        }

        private static bool _HasWaitingRequests(LoadContext load)
        {
            foreach (var request in load.PendingRequests)
                if (!request.CallerToken.IsCancellationRequested) return true;
            return false;
        }

        private void _RemoveWaitingRequest(LoadContext load, PendingResourceRequest request)
        {
            load.PendingRequests.Remove(request);
            if (_HasWaitingRequests(load) || load.Completion.Task.IsCompleted) return;
            if (load.DeliveryResource != null)
            {
                _FinishDelivery(load);
            }
            else
            {
                _AbandonLoad(load);
            }
        }

        /// <summary> 等待者领完或退出后，解除临时保护；长期记录不保留交付历史。 </summary>
        private void _FinishDelivery(LoadContext load)
        {
            var entry = load.DeliveryResource!;
            load.DeliveryResource = null;
            _RemoveLoadContext(load);
            if (entry.HoldCount == 0)
            {
                if (load.DeliveredToCaller) _ReleaseUnusedResource(entry);
                else _BeginUnload(entry); // 本轮没有任何请求能使用产物，直接收尾。
            }
        }

        private void _AbandonLoad(LoadContext load)
        {
            load.IsAbandoned = true;
            try { load.Cancellation?.Cancel(); }
            catch (Exception ex)
            {
                // 取消回调失败不应中断其他资源的退出清理，也不代表实际加载已经结束。
                _diagnostics.Report(new LoadError(DiagnosticCodes.ObserverCallbackFailed,
                    LoadStage.WaitForLoad, CleanupStatus.Unknown, ex, _LoadDiagnosticContext(load)));
            }
            if (!load.HasStarted)
            {
                // 尚未开始真实操作，取消后可以直接结束。
                _DisposeLoadCancellation(load);
                _RemoveLoadContext(load);
            }
        }

        private void _RemoveLoadContext(LoadContext load)
        {
            if (_loading.TryGetValue(load.Key, out var current) && ReferenceEquals(current, load))
                _loading.Remove(load.Key);
            load.Completion.TrySetResult(true);
        }

        #endregion

        #region 加载执行与提交 (§6.4)

        /// <summary> 资源锁由操作持有到加载及失败清理结束；调用者只异步等待，不负责提前开锁。 </summary>
        private async Task _RunLoadAsync(LoadContext load, LoaderRegistration registration,
            KeyedAsyncLock<ResourceKey>.Releaser resourceLock)
        {
            using (resourceLock)
            {
                LoadedAsset? result = null;
                LoadError? failure = null;
                var cancelled = false;
                var slots = registration.LoadSlots;
                var acquired = false;
                var operationToken = load.Cancellation!.Token;
                try
                {
                    if (slots != null)
                    {
                        await slots.WaitAsync(operationToken).ConfigureAwait(false);
                        acquired = true;
                    }
                    result = await registration.Loader.LoadAsync(
                        load.Resolved, new LoadProgressReporter(this, load), operationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                {
                    cancelled = true; // 加载器确认取消前已经回退本次取得的资源。
                }
                catch (Exception ex)
                {
                    failure = ex is ResourceLoadException rle ? rle.Error : new LoadError(
                        DiagnosticCodes.InternalUnexpected, LoadStage.LoadAsset,
                        CleanupStatus.Unknown, ex, _LoadDiagnosticContext(load));
                }
                finally
                {
                    if (acquired) slots!.Release();
                }

                var releaseResult = false;
                await _context.RunAsync(() =>
                {
                    _DisposeLoadCancellation(load);
                    if (cancelled)
                    {
                        _RemoveLoadContext(load);
                    }
                    else if (failure != null)
                    {
                        _RecordLoadFailure(load, failure, resourceLock, registration.Policy.FailurePolicy);
                    }
                    else if (result == null)
                    {
                        _RecordLoadFailure(load, _InvalidResult(load), resourceLock, registration.Policy.FailurePolicy);
                    }
                    else
                    {
                        if (!ResourceStore.IsAssetAlive(result))
                        {
                            load.Error = _InvalidResult(load);
                            releaseResult = true;
                        }
                        else
                        {
                            releaseResult = load.IsAbandoned || _isDisposed() || !_HasWaitingRequests(load);
                        }
                        if (releaseResult)
                        {
                            // 无接收者或无效结果不进入仓库，在当前资源锁内回退。
                            load.IsAbandoned = true;
                            load.IsCleaningUp = true;
                        }
                        else
                        {
                            var entry = new ResourceEntry(load.Key, result, load.MemoryPolicy);
                            _store.AddEntry(entry);
                            // 这是临时交付保护，不计入业务 HoldCount；仓库不会反向引用上下文。
                            load.DeliveryResource = entry;
                        }
                    }
                }).ConfigureAwait(false);

                if (releaseResult)
                {
                    var cleanupError = await _ReleaseAssetAsync(result!, _LoadDiagnosticContext(load)).ConfigureAwait(false);
                    await _context.RunAsync(() =>
                    {
                        var error = load.Error;
                        if (cleanupError != null)
                            error = error != null ? error.WithRelated(cleanupError) : cleanupError;
                        if (error != null)
                            _RecordLoadFailure(load, error, resourceLock, registration.Policy.FailurePolicy);
                        else
                            _RemoveLoadContext(load);
                    }).ConfigureAwait(false);
                }
            }
        }

        private LoadError _InvalidResult(LoadContext load) => new LoadError(
            DiagnosticCodes.LoaderInvalidResult, LoadStage.LoadAsset, CleanupStatus.Complete, null, _LoadDiagnosticContext(load));

        private Dictionary<string, object> _ContextOf(ResourceEntry entry)
        {
            return new Dictionary<string, object>
            {
                { "source", entry.Key.Source },
                { "key", entry.Key.ToString() },
            };
        }

        private static Dictionary<string, object> _LoadDiagnosticContext(LoadContext load) =>
            new Dictionary<string, object>
            {
                { "source", load.Key.Source },
                { "key", load.Key.ToString() },
                { "operationId", load.OperationId },
            };

        private void _RecordLoadFailure(LoadContext load, LoadError error,
            KeyedAsyncLock<ResourceKey>.Releaser resourceLock, LoadFailurePolicy failurePolicy)
        {
            load.Error = error;
            // 先通知旧批次，再移除上下文，避免新请求被划入上一轮失败。
            if (failurePolicy == LoadFailurePolicy.FailWaitingRequests || error.Cleanup != CleanupStatus.Complete)
                resourceLock.FailWaitingRequests(new ResourceLoadException(error));
            if (error.Cleanup != CleanupStatus.Complete)
                _loadCleanupErrors[load.Key] = error;
            _RemoveLoadContext(load);
            _diagnostics.Report(error);
        }

        #endregion

        #region 持有、空闲与卸载 (§6.5、§9.3)

        internal ResourceRef<T> Retain<T>(ResourceEntry entry) where T : class
        {
            if (_isDisposed())
            {
                throw new ObjectDisposedException(nameof(ResourceManager));
            }
            if (!_store.IsLive(entry))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetInvalidated, LoadStage.LoadAsset,
                    CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "key", entry.Key.ToString() } }));
            }
            _store.AddReference(entry);
            return new ResourceRef<T>(this, entry, _NextReferenceId());
        }

        internal void Release(ResourceEntry entry, long leaseId)
        {
            // 退出后凭证已作废，底层资源由 Dispose 统一尝试释放，晚归还不再驱动引用/缓存流程。
            if (_isDisposed()) return;
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
            if (_store.RemoveReference(entry) != 0)
            {
                return;
            }

            _ReleaseUnusedResource(entry);
        }

        /// <summary> 最后引用归还与最后等待者退出，都从这里决定缓存或卸载。 </summary>
        private void _ReleaseUnusedResource(ResourceEntry entry)
        {
            if (_loading.TryGetValue(entry.Key, out var load) && ReferenceEquals(load.DeliveryResource, entry))
                return; // 本轮交付还没结束，临时上下文保护这份产物。
            var memoryPolicy = entry.MemoryPolicy;
            // 加载器独立内存策略 (R25)：MaxIdleEntries=0 表示该加载器不保留空闲资源；
            // TTL 作用域为条目策略，条目上限/字节预算按系统默认策略执行
            if (_isDisposed() || !_store.IsLive(entry) || memoryPolicy.IdleLifetime <= TimeSpan.Zero
                || memoryPolicy.MaxIdleEntries <= 0 || _defaultMemory.MaxIdleEntries <= 0)
            {
                _BeginUnload(entry);
                return;
            }

            _store.AddIdle(entry, _monotonicNow());
            _EnforceBudget();
        }

        private void _BeginUnload(ResourceEntry entry, bool ignoreReferences = false)
        {
            if (!ignoreReferences && entry.HoldCount != 0)
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
            _store.MarkUnloading(entry);
            _ = _RunUnloadAsync(entry);
        }

        private async Task _RunUnloadAsync(ResourceEntry entry)
        {
            using (var resourceLock = await _resourceLocks.LockAsync(entry.Key).ConfigureAwait(false))
            {
                var error = await _ReleaseAssetAsync(entry.Asset, _ContextOf(entry)).ConfigureAwait(false);
                await _context.RunAsync(() =>
                {
                    if (error == null)
                    {
                        _store.RemoveEntry(entry);
                        entry.RemovalCompletion.TrySetResult(true);
                    }
                    else
                    {
                        _store.MarkReleaseFailed(entry, error);
                        entry.RemovalCompletion.TrySetException(new ResourceLoadException(error));
                        _diagnostics.Report(error);
                    }
                }).ConfigureAwait(false);
            }
        }

        /// <summary> 两条释放路径共用后端调用；是否通知加载等待者由各自流程决定。 </summary>
        private static async Task<LoadError?> _ReleaseAssetAsync(LoadedAsset asset, Dictionary<string, object> diagnosticContext)
        {
            try
            {
                await asset.ReleaseAsync().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return new LoadError(DiagnosticCodes.LifecycleReleaseFailed,
                    LoadStage.ReleaseAsset, CleanupStatus.Incomplete, ex, diagnosticContext);
            }
        }

        private static void _DisposeLoadCancellation(LoadContext load)
        {
            load.Cancellation?.Dispose();
            load.Cancellation = null;
        }

        #endregion

        #region 空闲缓存维护 (P2)

        private void _EnforceBudget()
        {
            if (_defaultMemory.MaxIdleEntries > 0)
            {
                while (_store.IdleCount > _defaultMemory.MaxIdleEntries)
                {
                    var oldest = _store.OldestIdle!;
                    _BeginUnload(oldest);
                }
            }
            if (_defaultMemory.MaxEstimatedIdleBytes is long cap)
            {
                var sum = _store.EstimatedIdleBytes;
                while (sum > cap && _store.IdleCount > 0)
                {
                    var oldest = _store.OldestIdle!;
                    sum -= oldest.EstimatedBytes;
                    _BeginUnload(oldest);
                }
            }
        }

        internal void TickMaintenance()
        {
            var now = _monotonicNow();
            foreach (var entry in _store.SnapshotIdleEntries())
            {
                if (now - entry.IdleSince >= entry.MemoryPolicy.IdleLifetime.TotalSeconds)
                {
                    _BeginUnload(entry);
                }
            }
            _EnforceBudget();
        }

        /// <summary> 立即卸载全部空闲条目；返回本批次的清理完成任务。 </summary>
        internal List<Task> UnloadAllIdle()
        {
            var tasks = new List<Task>();
            foreach (var entry in _store.SnapshotIdleEntries())
            {
                tasks.Add(entry.RemovalTask);
                _BeginUnload(entry);
            }
            return tasks;
        }

        #endregion

        #region 退出：取消加载，释放所有已登记产物

        internal void CancelPendingLoads()
        {
            // 内联上下文中，取消和卸载都可能同步完成，因此遍历快照。
            foreach (var load in new List<LoadContext>(_loading.Values))
            {
                if (load.DeliveryResource != null) _FinishDelivery(load);
                else _AbandonLoad(load);
            }
        }

        /// <summary> 实例操作已收尾后调用；退出时不以业务引用归零为释放条件。 </summary>
        internal void Dispose()
        {
            foreach (var entry in _store.SnapshotEntries())
                _BeginUnload(entry, ignoreReferences: true);
        }

        #endregion

        #region 加载与产物的联合查询（只用于诊断，不复制运行状态）

        internal List<LoadError> CollectStuckErrors()
        {
            var errors = _store.CollectStuckErrors();
            errors.AddRange(_loadCleanupErrors.Values);
            return errors;
        }

        internal List<ResourceRow> SnapshotRows()
        {
            var rows = new List<ResourceRow>();
            foreach (var entry in _store.SnapshotEntries())
            {
                var waiting = _loading.TryGetValue(entry.Key, out var load)
                    && ReferenceEquals(load.DeliveryResource, entry) ? load.PendingRequests.Count : 0;
                rows.Add(new ResourceRow(entry.Key, entry.State, entry.HoldCount, waiting, entry.EstimatedBytes));
            }
            foreach (var load in _loading.Values)
            {
                if (load.DeliveryResource != null) continue;
                var state = load.IsCleaningUp ? ResourceState.Unloading
                    : load.IsAbandoned ? ResourceState.Draining : ResourceState.Loading;
                rows.Add(new ResourceRow(load.Key, state, 0, load.PendingRequests.Count, 0));
            }
            foreach (var failure in _loadCleanupErrors)
                rows.Add(new ResourceRow(failure.Key, ResourceState.ReleaseFailed, 0, 0, 0));
            return rows;
        }

        #endregion

        private static void _ValidateResolved(ResolvedResource resolved, string address)
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
            if (_isDisposed()) throw new ObjectDisposedException(nameof(ResourceManager));
            if (!_store.IsLive(entry))
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
            return !_isDisposed() && _store.IsLive(entry);
        }

        #endregion

        private static LoadError _ManagerDisposed()
        {
            return new LoadError(DiagnosticCodes.LifecycleManagerDisposed, LoadStage.Dispose,
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
        /// 一轮加载的临时数据。后端结束且等待者领完/退出后移除；仓库和业务引用不持有它。
        /// DeliveryResource 仅在交付期间保护具体产物，其有效性始终查询 ResourceStore。
        /// </summary>
        private sealed class LoadContext
        {
            public readonly ResourceKey Key;
            public readonly ResolvedResource Resolved;
            public readonly string OperationId;
            public readonly MemoryPolicy MemoryPolicy;
            public readonly List<PendingResourceRequest> PendingRequests = new List<PendingResourceRequest>();
            public readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenSource? Cancellation = new CancellationTokenSource();
            public bool HasStarted;
            public LoadError? Error;
            public bool IsAbandoned;
            public bool IsCleaningUp;
            public ResourceEntry? DeliveryResource;
            // 只用于本轮结束时处理无人领取的结果，不进入长期产物记录。
            public bool DeliveredToCaller;

            public LoadContext(ResourceKey key, ResolvedResource resolved, string operationId, MemoryPolicy memoryPolicy)
            {
                Key = key;
                Resolved = resolved;
                OperationId = operationId;
                MemoryPolicy = memoryPolicy;
            }
        }

        private sealed class PendingResourceRequest
        {
            public readonly IProgress<ResourceProgress>? Progress;
            public readonly CancellationToken CallerToken;

            public PendingResourceRequest(IProgress<ResourceProgress>? progress, CancellationToken callerToken)
            {
                Progress = progress;
                CallerToken = callerToken;
            }
        }

        /// <summary>
        /// 进度扇出：在上下文内快照等待者监听器，在状态提交之外执行回调；
        /// 监听器抛异常不得让加载失败 (归类为观察者故障)。
        /// </summary>
        private sealed class LoadProgressReporter : IProgress<ResourceProgress>
        {
            private readonly LoadManager _owner;
            private readonly LoadContext _load;

            public LoadProgressReporter(LoadManager owner, LoadContext load)
            {
                _owner = owner;
                _load = load;
            }

            public void Report(ResourceProgress value)
            {
                // 上报者只投递通知，不等待主线程。先快照，避免订阅者重入改变集合。
                _owner._context.Post(() =>
                {
                    foreach (var target in _Snapshot())
                    {
                        if (target == null) continue;
                        try { target.Report(value); }
                        catch (Exception ex)
                        {
                            _owner._diagnostics.Report(new LoadError(
                                DiagnosticCodes.ObserverCallbackFailed, value.Stage,
                                CleanupStatus.Complete, ex));
                        }
                    }
                });
            }

            private IProgress<ResourceProgress>?[] _Snapshot()
            {
                var list = new IProgress<ResourceProgress>?[_load.PendingRequests.Count];
                for (var i = 0; i < _load.PendingRequests.Count; i++)
                {
                    list[i] = _load.PendingRequests[i].Progress;
                }
                return list;
            }
        }
    }

    /// <summary> 来源的加载器注册项：加载器 + 策略快照 (已冻结) + 加载并发槽位 </summary>
    internal sealed class LoaderRegistration
    {
        public readonly ResourceSource Source;
        public readonly IResourceLoader Loader;
        public readonly LoaderPolicy Policy;
        public readonly SemaphoreSlim? LoadSlots;

        public LoaderRegistration(ResourceSource source, IResourceLoader loader, LoaderPolicy policy)
        {
            Source = source;
            Loader = loader;
            Policy = policy;
            // 0 表示不限制并发。
            var max = Policy.MaxConcurrentLoads;
            LoadSlots = max > 0 ? new SemaphoreSlim(max, max) : null;
        }
    }
}
