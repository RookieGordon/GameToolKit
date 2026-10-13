/*
 * author       : Gordon
 * datetime     : 2026/10/10
 * description  : 加载器路由与资源生命周期编排。
 *                路由/解析 → 复用或共享加载 → 交付引用 → 归还后等待业务卸载。
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
        // 保存具体释放残留；不按 key 封锁后续加载，也不自动重试释放。
        private readonly List<DirtyResource> _dirtyResources = new List<DirtyResource>();
        private readonly Dictionary<ResourceSource, LoaderRegistration> _loaders =
            new Dictionary<ResourceSource, LoaderRegistration>();
        private readonly IResourceDiagnostics _diagnostics;
        private readonly Func<bool> _isDisposed;

        private long _leaseSeed;
        private long _operationSeed;

        internal LoadManager(
            IExecutionContext context,
            ResourceStore store,
            IResourceDiagnostics diagnostics,
            Func<bool> isDisposed)
        {
            _context = context;
            _store = store;
            _diagnostics = diagnostics;
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
                _loaders[source].Queue.Stop(); // 组装期替换，旧队列还没有加载任务。
                _loaders[source] = replacement;
            }
        }

        private LoaderRegistration _CreateRegistration(ResourceSource source, IResourceLoader loader, LoaderPolicy? policy)
        {
            var snapshot = policy != null ? LoaderPolicy.Clone(policy) : new LoaderPolicy();
            snapshot.Validate();
            return new LoaderRegistration(source, loader, snapshot,
                new LoaderQueue(snapshot.MaxConcurrentLoads, _ConsumeLoadAsync,
                    error => _diagnostics.Report(new LoadError(DiagnosticCodes.InternalUnexpected,
                        LoadStage.WaitForLoad, CleanupStatus.Unknown, error))));
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
        /// 生产请求：前置检查 → 缓存复用或订阅同次加载 → 首次请求入队 → 等待自己的交付。
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

                await _context.RunAsync(() =>
                {
                    _CheckRequest(callerToken);
                    if (_store.TryGetEntry(key, out var entry))
                    {
                        if (entry.State == ResourceState.Unloading)
                        {
                            previousFinished = entry.RemovalTask;
                            return;
                        }
                        if (_store.IsLive(entry))
                        {
                            cached = _AcquireLoadedReference<T>(entry);
                            return;
                        }
                        // 失效产物只退出复用索引；清理由业务另行发起。
                        _store.DetachEntry(entry);
                    }
                    if (_loading.TryGetValue(key, out operation))
                    {
                        if (operation.IsAbandoned) previousFinished = operation.Finished.Task;
                        else operation.Requests.Add(request);
                        return;
                    }

                    operation = new LoadOperation(key, resolved, registration,
                        "op-" + Interlocked.Increment(ref _operationSeed), request);
                    operation.Requests.Add(request);
                    _loading.Add(key, operation);
                    // 生产者只提交一份实际任务；队列消费者自行取得并执行。
                    registration.Queue.Enqueue(operation);
                }).ConfigureAwait(false);

                if (cached != null) return cached;
                if (previousFinished != null)
                {
                    await previousFinished.WaitWithCancellation(callerToken).ConfigureAwait(false);
                    continue;
                }

                using (callerToken.Register(() => { _ = _CancelRequestAsync(operation!, request); }))
                {
                    // 完成成功即引用所有权交付，不能再用取消等待丢弃已经分配的引用。
                    var reference = await request.Completion.Task.ConfigureAwait(false);
                    if (reference != null)
                    {
                        return reference;
                    }
                }
            }
        }

        /// <summary>消费者取得任务后登记开始，再在状态提交之外调用加载器。</summary>
        private async Task _ConsumeLoadAsync(LoadOperation operation)
        {
            try
            {
                var canStart = await _context.RunAsync(() =>
                {
                    // 排队取消已通知请求并移除加载记录；队列中的旧项只需跳过。
                    if (operation.Finished.Task.IsCompleted) return false;
                    for (var i = operation.Requests.Count - 1; i >= 0; i--)
                    {
                        var request = operation.Requests[i];
                        if (!request.CallerToken.IsCancellationRequested) continue;
                        request.Cancel();
                        operation.Requests.RemoveAt(i);
                    }
                    if (_isDisposed())
                    {
                        _NotifyLoadFailure(operation, _ManagerDisposed());
                        return false;
                    }
                    if (operation.Requests.Count == 0)
                    {
                        _AbandonLoad(operation);
                        return false;
                    }
                    operation.HasStarted = true;
                    return true;
                }).ConfigureAwait(false);
                if (canStart)
                {
                    await _RunLoadAsync(operation).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                // 后端错误在本轮流程内处理；此处防止意外编排错误遗失请求和消费者。
                await _context.RunAsync(() =>
                {
                    if (!operation.Finished.Task.IsCompleted)
                        _NotifyLoadFailure(operation, new LoadError(DiagnosticCodes.InternalUnexpected,
                            LoadStage.WaitForLoad, CleanupStatus.Unknown, error, _LoadDiagnosticContext(operation)));
                }).ConfigureAwait(false);
            }
        }

        private void _CheckRequest(CancellationToken callerToken)
        {
            callerToken.ThrowIfCancellationRequested();
            if (_isDisposed()) throw new ResourceLoadException(_ManagerDisposed());
        }

        private long _NextReferenceId() => Interlocked.Increment(ref _leaseSeed);

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
            if (!operation.HasStarted)
            {
                _FinishLoad(operation); // 尚未开始后端；消费者以后取到队列项时跳过。
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
            IReadOnlyList<object>? cleanupRemainders = null;
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
                if (ex is ResourceLoadException loadException)
                {
                    failure = loadException.Error;
                    cleanupRemainders = loadException.CleanupRemainders;
                }
                else failure = new LoadError(DiagnosticCodes.InternalUnexpected, LoadStage.LoadAsset,
                    CleanupStatus.Unknown, ex, _LoadDiagnosticContext(operation));
            }

            var releaseResult = false;
            await _context.RunAsync(() =>
            {
                if (failure != null)
                {
                    if (cleanupRemainders != null && cleanupRemainders.Count > 0)
                        RecordCleanupFailure(operation.Key, failure, cleanupRemainders);
                    _NotifyLoadFailure(operation, failure);
                    return;
                }
                if (result == null)
                {
                    if (token.IsCancellationRequested) _FinishLoad(operation);
                    else _NotifyLoadFailure(operation, _InvalidResult(operation));
                    return;
                }
                if (!ResourceStore.IsAssetAlive(result)) failure = _InvalidResult(operation);
                if (failure == null && !operation.IsAbandoned && !_isDisposed())
                {
                    if (_NotifyLoadSuccess(operation, result)) return;
                }
                operation.IsAbandoned = true;
                operation.IsCleaningUp = true;
                releaseResult = true;
            }).ConfigureAwait(false);

            if (releaseResult)
            {
                var cleanup = await _ReleaseAssetAsync(operation.Key, result!, _LoadDiagnosticContext(operation)).ConfigureAwait(false);
                await _context.RunAsync(() =>
                {
                    if (cleanup != null)
                    {
                        _dirtyResources.Add(cleanup);
                        failure = failure != null ? failure.WithRelated(cleanup.Error) : cleanup.Error;
                    }
                    if (failure != null) _NotifyLoadFailure(operation, failure);
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

        /// <summary>同次成功批量建立独立引用后通知；无人接收时产物仍归原操作收尾。</summary>
        private bool _NotifyLoadSuccess(LoadOperation operation, LoadedAsset result)
        {
            var entry = new ResourceEntry(operation.Key, result);
            _store.AddEntry(entry);
            foreach (var request in operation.Requests) request.CompleteWithResource(this, entry);
            if (entry.HoldCount > 0)
            {
                _FinishLoad(operation);
                return true;
            }
            _store.RemoveEntry(entry);
            return false;
        }

        /// <summary>同次失败按场景通知；个人取消、本轮失败与后继重试分别完成各自请求。</summary>
        private void _NotifyLoadFailure(LoadOperation operation, LoadError error)
        {
            foreach (var request in operation.Requests)
            {
                if (request.CallerToken.IsCancellationRequested) request.Cancel();
                else if (error.DiagnosticCode == DiagnosticCodes.LifecycleManagerDisposed
                    || ReferenceEquals(request, operation.AttemptRequest)
                    || operation.Registration.Policy.FailurePolicy == LoadFailurePolicy.FailWaitingRequests) request.Fail(error);
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

        /// <summary> 将已有持有的一份具体所有权分给清理残留；不查询产物存活，不调用后端。 </summary>
        internal ResourceRef<T> RetainForCleanup<T>(ResourceEntry entry) where T : class
        {
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

            entry.State = ResourceState.Idle;
        }

        /// <summary> 只登记卸载；成功返回后由调用者在状态提交之外启动释放。 </summary>
        private bool _BeginUnload(ResourceEntry entry, bool ignoreReferences = false)
        {
            if (!_store.ContainsEntry(entry) || entry.State == ResourceState.Unloading
                || (!ignoreReferences && entry.HoldCount != 0)) return false;
            entry.State = ResourceState.Unloading;
            return true;
        }

        private async Task _RunUnloadAsync(ResourceEntry entry)
        {
            var failure = await _ReleaseAssetAsync(entry.Key, entry.Asset, _ContextOf(entry)).ConfigureAwait(false);
            await _context.RunAsync(() =>
            {
                if (failure != null)
                {
                    _dirtyResources.Add(failure);
                    _diagnostics.Report(failure.Error);
                }
                _store.RemoveEntry(entry);
                entry.RemovalCompletion.TrySetResult(failure);
            }).ConfigureAwait(false);
        }

        private static async Task<DirtyResource?> _ReleaseAssetAsync(
            ResourceKey key, LoadedAsset asset, Dictionary<string, object> diagnosticContext)
        {
            try
            {
                await asset.ReleaseAsync().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                var error = new LoadError(DiagnosticCodes.LifecycleReleaseFailed,
                    LoadStage.ReleaseAsset, CleanupStatus.Incomplete, ex, diagnosticContext);
                // 显式残留精确指出未释放的对象；否则保留产物及其拥有释放依赖的回调。
                var retained = ex is ResourceLoadException failed && failed.CleanupRemainders.Count > 0
                    ? failed.CleanupRemainders : new object[] { asset.Value, asset };
                return new DirtyResource(key, error, retained);
            }
        }

        #endregion

        #region 业务卸载：选定范围 → 检查持有 → 释放 → 汇总结果

        internal async Task<ResourceUnloadResult> UnloadUnusedAsync(
            Predicate<ResourceRow>? select, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await _context.RunAsync(() =>
            {
                var items = new List<(ResourceEntry Entry, ResourceRow Row)>();
                foreach (var entry in _store.SnapshotEntries())
                    items.Add((entry, new ResourceRow(entry.Key, entry.State, entry.HoldCount, 0)));
                return items;
            }).ConfigureAwait(false);

            // 业务选择器可能查询或重新取得资源；执行时不能占用内部状态提交。
            var selected = new List<ResourceEntry>();
            foreach (var item in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (select == null || select(item.Row)) selected.Add(item.Entry);
            }

            var started = new List<ResourceEntry>();
            var completions = new List<Task<DirtyResource?>>();
            var skipped = 0;
            await _context.RunAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in selected)
                {
                    if (!_store.ContainsEntry(entry)) continue;
                    // 快照之后可能已被重新持有，必须以这份具体记录的当前状态为准。
                    if (entry.HoldCount != 0) { skipped++; continue; }
                    completions.Add(entry.RemovalTask);
                    if (_BeginUnload(entry)) started.Add(entry);
                }
            }).ConfigureAwait(false);
            foreach (var entry in started) _ = _RunUnloadAsync(entry);

            // 取消只停止调用者等待；已开始的释放仍独立完成并移交残留。
            var allReleases = Task.WhenAll(completions);
            await allReleases.WaitWithCancellation(cancellationToken).ConfigureAwait(false);
            var outcomes = await allReleases.ConfigureAwait(false);
            var failures = new List<DirtyResource>();
            foreach (var failure in outcomes) if (failure != null) failures.Add(failure);
            return new ResourceUnloadResult(outcomes.Length - failures.Count, skipped, failures);
        }

        internal void RecordCleanupFailure(ResourceKey key, LoadError error, IReadOnlyList<object> retainedObjects)
        {
            if (retainedObjects.Count > 0) _dirtyResources.Add(new DirtyResource(key, error, retainedObjects));
        }

        internal List<DirtyResource> SnapshotDirtyResources() => new List<DirtyResource>(_dirtyResources);

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
            // 先通知所有请求，再停止空闲消费者；执行中的后端仍完成自己的实际收尾。
            lock (_loaders)
                foreach (var registration in _loaders.Values) registration.Queue.Stop();
            foreach (var operation in cancellations) _CancelBackend(operation);
        }

        /// <summary> 实例操作已收尾后调用；退出时不以业务引用归零为释放条件。 </summary>
        internal async Task DisposeAsync()
        {
            var entries = await _context.RunAsync(() =>
            {
                var started = new List<ResourceEntry>();
                foreach (var entry in _store.SnapshotEntries())
                    if (_BeginUnload(entry, ignoreReferences: true)) started.Add(entry);
                return started;
            }).ConfigureAwait(false);

            // 先完成全部登记，再调用释放；同步释放不必等下一帧才能执行。
            var releases = new List<Task>();
            foreach (var entry in entries) releases.Add(_RunUnloadAsync(entry));
            await Task.WhenAll(releases).ConfigureAwait(false);
        }

        #endregion

        #region 加载与产物的联合查询（只用于诊断，不复制运行状态）

        internal List<ResourceRow> SnapshotRows()
        {
            var rows = new List<ResourceRow>();
            foreach (var entry in _store.SnapshotEntries())
            {
                rows.Add(new ResourceRow(entry.Key, entry.State, entry.HoldCount, 0));
            }
            foreach (var load in _loading.Values)
            {
                var state = load.IsCleaningUp ? ResourceState.Unloading
                    : load.IsAbandoned ? ResourceState.Draining : ResourceState.Loading;
                rows.Add(new ResourceRow(load.Key, state, 0, load.Requests.Count));
            }
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
            // 只在执行上下文内改变；出队不等于后端已经开始。
            public bool HasStarted;
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

    /// <summary>来源的加载器、策略快照与独立消费队列。</summary>
    internal sealed class LoaderRegistration
    {
        public readonly ResourceSource Source;
        public readonly IResourceLoader Loader;
        public readonly LoaderPolicy Policy;
        internal readonly LoaderQueue Queue;

        public LoaderRegistration(ResourceSource source, IResourceLoader loader, LoaderPolicy policy, LoaderQueue queue)
        {
            Source = source;
            Loader = loader;
            Policy = policy;
            Queue = queue;
        }
    }
}
