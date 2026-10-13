/*
 * author       : Gordon
 * datetime     : 2026/10/10
 * description  : 加载器路由与资源生命周期编排。
 *                路由/解析 → 复用或共享加载 → 交付引用 → 归还后缓存或卸载。
 *                请求合并为临时任务，由消费者执行；ResourceStore 只保存实际产物与引用。
 *                状态在执行上下文中短暂提交，排队、加载和卸载均不阻塞线程。
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
        private readonly Dictionary<ResourceKey, LoadOperation> _loading = new Dictionary<ResourceKey, LoadOperation>();
        // 没有产物但清理未确认完成的加载，只保留错误证据来阻止同键重试。
        private readonly Dictionary<ResourceKey, LoadError> _loadCleanupErrors = new Dictionary<ResourceKey, LoadError>();
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
            lock (_loaders)
            {
                if (_loaders.ContainsKey(source))
                    throw new InvalidOperationException($"资源来源已注册加载器: {source}；不能重复注册");
                _loaders.Add(source, _CreateRegistration(source, loader, policy));
            }
        }

        internal void ReplaceLoader(ResourceSource source, IResourceLoader loader, LoaderPolicy? policy)
        {
            lock (_loaders)
            {
                if (!_loaders.ContainsKey(source))
                    throw new InvalidOperationException($"资源来源尚未注册加载器: {source}，请先 RegisterLoader");
                var replacement = _CreateRegistration(source, loader, policy);
                _loaders[source] = replacement;
            }
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
            // 路由只读取注册表，不调用 Unity API；与运行期新增注册使用同一个短锁。
            lock (_loaders)
            {
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
                registration, 
                token => registration.Loader.ResolveAsync(request, token),
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
        /// 生产请求：前置检查 → 缓存复用或合并入队 → 启动消费者 → 等待自己的交付。
        /// null 完成仅表示远端本轮失败后，后继请求可以重新解析并参加下一轮。
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
                _ValidateResolved(resolved, address);
                var key = new ResourceKey(registration.Source, resolved.LocalKey);
                var request = new PendingResourceRequest<T>(progress, callerToken);
                LoadOperation? operation = null;
                ResourceRef<T>? cached = null;
                Task? previousFinished = null;
                LoadOperation? consumer = null;

                await _context.RunAsync(() =>
                {
                    _CheckRequest(callerToken);
                    if (_loadCleanupErrors.TryGetValue(key, out var error))
                        throw _PreviousCleanupFailure(error);
                    if (_store.TryGetEntry(key, out var entry))
                    {
                        if (_store.IsLive(entry)) cached = _AcquireLoadedReference<T>(entry);
                        else previousFinished = _RetireUnavailableResourceBeforeLoad(entry);
                        return;
                    }
                    if (_loading.TryGetValue(key, out operation))
                    {
                        if (operation.IsAbandoned)
                        {
                            previousFinished = operation.Finished.Task;
                            return;
                        }
                    }
                    else
                    {
                        operation = new LoadOperation(key, resolved, registration,
                            "op-" + Interlocked.Increment(ref _operationSeed), request);
                        _loading.Add(key, operation);
                        operation.QueueNode = registration.PendingLoads.AddLast(operation);
                    }
                    operation.Requests.Add(request);
                    // 一个资源只排一份任务；并发槽位属于消费者，不属于业务请求。
                    consumer = _TakeNextLoad(registration);
                }).ConfigureAwait(false);

                if (cached != null) return cached;
                if (previousFinished != null)
                {
                    try { await previousFinished.WaitWithCancellation(callerToken).ConfigureAwait(false); }
                    catch (ResourceLoadException ex) { throw _PreviousCleanupFailure(ex.Error); }
                    continue;
                }

                using (callerToken.Register(() => { _ = _CancelRequestAsync(operation!, request); }))
                {
                    // 登记后即承担启动责任；个人取消不能使其他请求或已预留的槽位悬挂。
                    if (consumer != null) _ = _ConsumeLoadsAsync(consumer);
                    // 完成成功即引用所有权交付，不能再用取消等待丢弃已经分配的引用。
                    var reference = await request.Completion.Task.ConfigureAwait(false);
                    if (reference != null) return reference;
                }
            }
        }

        /// <summary> 状态提交内选取一份任务。返回后由调用方在提交之外启动后端。 </summary>
        private LoadOperation? _TakeNextLoad(LoaderRegistration registration)
        {
            if (_isDisposed() || registration.PendingLoads.First == null
                || (registration.Policy.MaxConcurrentLoads > 0
                    && registration.ActiveLoads >= registration.Policy.MaxConcurrentLoads)) return null;
            var operation = registration.PendingLoads.First.Value;
            registration.PendingLoads.RemoveFirst();
            operation.QueueNode = null;
            registration.ActiveLoads++;
            return operation;
        }

        /// <summary> 一个消费者执行完当前任务再取下一份；同步完成也只循环，不递归启动。 </summary>
        private async Task _ConsumeLoadsAsync(LoadOperation? operation)
        {
            while (operation != null)
            {
                var registration = operation.Registration;
                await _RunLoadAsync(operation).ConfigureAwait(false);
                await _context.RunAsync(() =>
                {
                    registration.ActiveLoads--;
                    operation = _TakeNextLoad(registration);
                }).ConfigureAwait(false);
                // 后端可能从取消/池回调中同步完成。下一份任务让出当前调用栈后再执行。
                if (operation != null) await Task.Yield();
            }
        }

        private void _CheckRequest(CancellationToken callerToken)
        {
            callerToken.ThrowIfCancellationRequested();
            if (_isDisposed()) throw new ResourceLoadException(_ManagerDisposed());
        }

        private long _NextReferenceId() => Interlocked.Increment(ref _leaseSeed);

        /// <summary> 仅用于加载前：清退失效产物；仍被持有时由最后一次归还触发释放。 </summary>
        private Task _RetireUnavailableResourceBeforeLoad(ResourceEntry entry)
        {
            if (entry.State == ResourceState.ReleaseFailed)
                throw _PreviousCleanupFailure(entry.CleanupError!);
            if (entry.HoldCount == 0) _ReleaseUnusedResource(entry);
            return entry.RemovalTask;
        }

        private static ResourceLoadException _PreviousCleanupFailure(LoadError error)
        {
            var context = new Dictionary<string, object>();
            foreach (var pair in error.Context) context.Add(pair.Key, pair.Value);
            context["loadBlockedByPreviousCleanup"] = true;
            // 保留原始故障与诊断身份，同时说明本请求在加载前已被拒绝。
            return new ResourceLoadException(new LoadError(error.DiagnosticCode, error.Stage, error.Cleanup,
                error.Cause, context, error.RelatedErrors, error.UserCode, error.DiagnosticId));
        }

        private ResourceRef<T> _AcquireLoadedReference<T>(ResourceEntry entry) where T : class
        {
            var asset = entry.Asset;
            if (!typeof(T).IsInstanceOfType(asset.Value))
                throw new ResourceLoadException(_TypeMismatch(entry, typeof(T), asset.Value));
            _store.AddReference(entry);
            return new ResourceRef<T>(this, entry, _NextReferenceId());
        }

        private async Task _CancelRequestAsync(LoadOperation operation, PendingResourceRequest request)
        {
            var cancelBackend = false;
            await _context.RunAsync(() =>
            {
                // 交付成功后的取消不撤销已归属调用者的引用。
                if (!operation.Requests.Remove(request)) return;
                request.Cancel();
                if (operation.Requests.Count == 0) cancelBackend = _AbandonLoad(operation);
            }).ConfigureAwait(false);
            if (cancelBackend) _CancelBackend(operation);
        }

        /// <summary>只登记放弃；返回 true 时，由调用方在提交之外通知后端取消。</summary>
        private bool _AbandonLoad(LoadOperation operation)
        {
            if (operation.Finished.Task.IsCompleted || operation.IsAbandoned) return false;
            operation.IsAbandoned = true;
            if (operation.QueueNode != null)
            {
                operation.Registration.PendingLoads.Remove(operation.QueueNode);
                operation.QueueNode = null;
                _FinishLoad(operation); // 尚未交给消费者，不存在等待收尾的底层操作。
                return false;
            }
            return true;
        }

        private void _CancelBackend(LoadOperation operation)
        {
            try { operation.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { } // 提交返回后，后端可能已完成并释放取消源。
            catch (Exception ex)
            {
                _diagnostics.Report(new LoadError(DiagnosticCodes.ObserverCallbackFailed,
                    LoadStage.WaitForLoad, CleanupStatus.Unknown, ex, _LoadDiagnosticContext(operation)));
            }
        }

        private void _FinishLoad(LoadOperation operation)
        {
            _loading.Remove(operation.Key);
            operation.Requests.Clear();
            operation.Cancellation.Dispose();
            operation.Finished.TrySetResult(true);
        }

        #endregion

        #region 消费加载任务：执行 → 批量交付或通知失败

        private async Task _RunLoadAsync(LoadOperation operation)
        {
            LoadedAsset? result = null;
            LoadError? failure = null;
            var token = operation.Cancellation.Token;
            try
            {
                _CheckRequest(token);
                result = await operation.Registration.Loader.LoadAsync(operation.Resolved,
                    new LoadProgressReporter(this, operation), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                failure = ex is ResourceLoadException rle ? rle.Error : new LoadError(
                    DiagnosticCodes.InternalUnexpected, LoadStage.LoadAsset,
                    CleanupStatus.Unknown, ex, _LoadDiagnosticContext(operation));
            }

            var releaseResult = false;
            await _context.RunAsync(() =>
            {
                if (failure != null)
                {
                    _CompleteFailedLoad(operation, failure);
                    return;
                }
                if (result == null)
                {
                    if (token.IsCancellationRequested) _FinishLoad(operation);
                    else _CompleteFailedLoad(operation, _InvalidResult(operation));
                    return;
                }
                if (!ResourceStore.IsAssetAlive(result)) failure = _InvalidResult(operation);
                if (failure == null && !operation.IsAbandoned && !_isDisposed())
                {
                    var entry = new ResourceEntry(operation.Key, result,
                        operation.Registration.Policy.Memory ?? _defaultMemory);
                    _store.AddEntry(entry);
                    // 同一次提交建立全部持有；通知之后不再有“已发布但尚待领取”的阶段。
                    foreach (var request in operation.Requests) request.CompleteWithResource(this, entry);
                    if (entry.HoldCount > 0)
                    {
                        _FinishLoad(operation);
                        return;
                    }
                    _store.RemoveEntry(entry); // 全部取消或类型不符，产物仍由本轮操作收尾。
                }
                operation.IsAbandoned = true;
                operation.IsCleaningUp = true;
                releaseResult = true;
            }).ConfigureAwait(false);

            if (releaseResult)
            {
                var cleanup = await _ReleaseAssetAsync(result!, _LoadDiagnosticContext(operation)).ConfigureAwait(false);
                await _context.RunAsync(() =>
                {
                    if (cleanup != null) failure = failure != null ? failure.WithRelated(cleanup) : cleanup;
                    if (failure != null) _CompleteFailedLoad(operation, failure);
                    else _FinishLoad(operation);
                }).ConfigureAwait(false);
            }
        }

        private LoadError _InvalidResult(LoadOperation load) => new LoadError(
            DiagnosticCodes.LoaderInvalidResult, LoadStage.LoadAsset, CleanupStatus.Complete, null, _LoadDiagnosticContext(load));

        private Dictionary<string, object> _ContextOf(ResourceEntry entry)
        {
            return new Dictionary<string, object>
            {
                { "source", entry.Key.Source },
                { "key", entry.Key.ToString() },
            };
        }

        private static Dictionary<string, object> _LoadDiagnosticContext(LoadOperation load) =>
            new Dictionary<string, object>
            {
                { "source", load.Key.Source },
                { "key", load.Key.ToString() },
                { "operationId", load.OperationId },
            };

        private void _CompleteFailedLoad(LoadOperation operation, LoadError error)
        {
            if (error.Cleanup != CleanupStatus.Complete) _loadCleanupErrors[operation.Key] = error;
            foreach (var request in operation.Requests)
            {
                if (request.CallerToken.IsCancellationRequested) request.Cancel();
                else if (ReferenceEquals(request, operation.AttemptRequest)
                    || operation.Registration.Policy.FailurePolicy == LoadFailurePolicy.FailWaitingRequests
                    || error.Cleanup != CleanupStatus.Complete) request.Fail(error);
                else request.Retry(); // 后继请求重新解析；不替失败请求在后台无限重试。
            }
            _FinishLoad(operation);
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

        /// <summary> 最后引用归还后决定缓存或卸载。 </summary>
        private void _ReleaseUnusedResource(ResourceEntry entry)
        {
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

        internal async Task CancelPendingLoadsAsync()
        {
            var cancellations = new List<LoadOperation>();
            await _context.RunAsync(() =>
            {
                foreach (var load in new List<LoadOperation>(_loading.Values))
                {
                    foreach (var request in load.Requests) request.Fail(_ManagerDisposed());
                    load.Requests.Clear();
                    if (_AbandonLoad(load)) cancellations.Add(load);
                }
            }).ConfigureAwait(false);
            foreach (var operation in cancellations) _CancelBackend(operation);
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
                rows.Add(new ResourceRow(entry.Key, entry.State, entry.HoldCount, 0, entry.EstimatedBytes));
            }
            foreach (var load in _loading.Values)
            {
                var state = load.IsCleaningUp ? ResourceState.Unloading
                    : load.IsAbandoned ? ResourceState.Draining : ResourceState.Loading;
                rows.Add(new ResourceRow(load.Key, state, 0, load.Requests.Count, 0));
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

        /// <summary>一份排队或执行中的任务；结果批量交付或失败通知后立即移除。</summary>
        internal sealed class LoadOperation
        {
            public readonly ResourceKey Key;
            public readonly ResolvedResource Resolved;
            public readonly LoaderRegistration Registration;
            public readonly string OperationId;
            public readonly PendingResourceRequest AttemptRequest;
            public readonly List<PendingResourceRequest> Requests = new List<PendingResourceRequest>();
            public readonly TaskCompletionSource<bool> Finished =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public LinkedListNode<LoadOperation>? QueueNode;
            public bool IsAbandoned;
            public bool IsCleaningUp;

            public LoadOperation(ResourceKey key, ResolvedResource resolved, LoaderRegistration registration,
                string operationId, PendingResourceRequest attemptRequest)
            {
                Key = key;
                Resolved = resolved;
                Registration = registration;
                OperationId = operationId;
                AttemptRequest = attemptRequest;
            }
        }

        /// <summary>每个业务请求有自己的完成任务与取消；不拥有底层操作。</summary>
        internal abstract class PendingResourceRequest
        {
            public readonly IProgress<ResourceProgress>? Progress;
            public readonly CancellationToken CallerToken;
            protected PendingResourceRequest(IProgress<ResourceProgress>? progress, CancellationToken callerToken)
            {
                Progress = progress;
                CallerToken = callerToken;
            }
            public abstract void CompleteWithResource(LoadManager owner, ResourceEntry entry);
            public abstract void Fail(LoadError error);
            public abstract void Retry();
            public abstract void Cancel();
        }

        private sealed class PendingResourceRequest<T> : PendingResourceRequest where T : class
        {
            public readonly TaskCompletionSource<ResourceRef<T>?> Completion =
                new TaskCompletionSource<ResourceRef<T>?>(TaskCreationOptions.RunContinuationsAsynchronously);

            public PendingResourceRequest(IProgress<ResourceProgress>? progress, CancellationToken callerToken)
                : base(progress, callerToken) { }

            public override void CompleteWithResource(LoadManager owner, ResourceEntry entry)
            {
                if (CallerToken.IsCancellationRequested) { Cancel(); return; }
                try { Completion.SetResult(owner._AcquireLoadedReference<T>(entry)); }
                catch (ResourceLoadException ex) { Completion.TrySetException(ex); }
            }
            public override void Fail(LoadError error) => Completion.TrySetException(new ResourceLoadException(error));
            public override void Retry() => Completion.TrySetResult(null);
            public override void Cancel() => Completion.TrySetCanceled(CallerToken);
        }

        /// <summary>
        /// 进度扇出：在上下文内快照等待者监听器，在状态提交之外执行回调；
        /// 监听器抛异常不得让加载失败 (归类为观察者故障)。
        /// </summary>
        private sealed class LoadProgressReporter : IProgress<ResourceProgress>
        {
            private readonly LoadManager _owner;
            private readonly LoadOperation _load;

            public LoadProgressReporter(LoadManager owner, LoadOperation load)
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
                var list = new IProgress<ResourceProgress>?[_load.Requests.Count];
                for (var i = 0; i < _load.Requests.Count; i++)
                {
                    list[i] = _load.Requests[i].Progress;
                }
                return list;
            }
        }
    }

    /// <summary> 来源的加载器注册项：加载器 + 策略快照 + 待消费任务与执行数量 </summary>
    internal sealed class LoaderRegistration
    {
        public readonly ResourceSource Source;
        public readonly IResourceLoader Loader;
        public readonly LoaderPolicy Policy;
        internal readonly LinkedList<LoadManager.LoadOperation> PendingLoads = new LinkedList<LoadManager.LoadOperation>();
        internal int ActiveLoads;

        public LoaderRegistration(ResourceSource source, IResourceLoader loader, LoaderPolicy policy)
        {
            Source = source;
            Loader = loader;
            Policy = policy;
        }
    }
}
