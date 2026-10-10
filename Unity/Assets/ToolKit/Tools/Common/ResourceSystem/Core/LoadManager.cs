/*
 * author       : Gordon
 * datetime     : 2026/10/10
 * description  : 加载器路由与资源生命周期编排。
 *                路由/解析 → 复用或共享加载 → 交付引用 → 归还后缓存或卸载。
 *                ResourceStore 保存唯一条目与索引，本类负责推进流程。
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
        private readonly KeyedAsyncLock<ResourceKey> _resourceLocks = new KeyedAsyncLock<ResourceKey>();
        private readonly string _defaultLoader;
        private readonly Dictionary<string, LoaderRegistration> _loaders =
            new Dictionary<string, LoaderRegistration>(StringComparer.Ordinal);
        private readonly IResourceDiagnostics _diagnostics;
        private readonly Func<double> _monotonicNow;
        private readonly MemoryPolicy _defaultMemory;
        private readonly Func<bool> _isRunning;
        private readonly Action _checkShutdownComplete;

        private long _leaseSeed;
        private long _operationSeed;

        internal LoadManager(
            IExecutionContext context,
            ResourceStore store,
            string defaultLoader,
            IResourceDiagnostics diagnostics,
            Func<double> monotonicNow,
            MemoryPolicy defaultMemory,
            Func<bool> isRunning,
            Action checkShutdownComplete)
        {
            _context = context;
            _store = store;
            _defaultLoader = defaultLoader;
            _diagnostics = diagnostics;
            _monotonicNow = monotonicNow;
            _defaultMemory = defaultMemory;
            _isRunning = isRunning;
            _checkShutdownComplete = checkShutdownComplete;
        }

        internal IExecutionContext Context => _context;

        #region 加载器注册与路由（配置期由 ResourceManager 统一限制）

        internal void RegisterLoader(string name, IResourceLoader loader, LoaderPolicy? policy)
        {
            if (_loaders.ContainsKey(name))
            {
                throw new InvalidOperationException($"加载器已注册: {name}，替换请使用 ReplaceLoader");
            }
            _loaders.Add(name, _CreateRegistration(name, loader, policy));
        }

        internal void ReplaceLoader(string name, IResourceLoader loader, LoaderPolicy? policy)
        {
            if (!_loaders.TryGetValue(name, out var previous))
            {
                throw new InvalidOperationException($"替换的加载器不存在: {name}，请先 RegisterLoader");
            }
            var replacement = _CreateRegistration(name, loader, policy);
            previous.LoadSlots?.Dispose();
            _loaders[name] = replacement;
        }

        private static LoaderRegistration _CreateRegistration(string name, IResourceLoader loader, LoaderPolicy? policy)
        {
            var snapshot = policy != null ? LoaderPolicy.Clone(policy) : new LoaderPolicy();
            snapshot.Validate();
            return new LoaderRegistration(name, loader, snapshot);
        }

        internal LoaderRegistration Route(string? name)
        {
            name ??= _defaultLoader;
            if (string.IsNullOrEmpty(name) || !_loaders.TryGetValue(name, out var registration))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderNotRegistered, LoadStage.Route, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "loaderId", name ?? "" },
                        { "registered", string.Join(",", _loaders.Keys) },
                    }));
            }
            return registration;
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
        /// 解析 → 登记领取请求 → 取得资源锁 → 复查/加载 → 领取引用。
        /// 只有旧资源退出或远端前一次尝试失败时重试；执行本次加载的请求收到自己的失败。
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
                var key = new ResourceKey(registration.Name, resolved.LocalKey);
                ResourceEntry entry = null!;
                ResourceRef<T>? reference = null;
                Task? previousRemoval = null;
                Task<KeyedAsyncLock<ResourceKey>.Releaser>? lockReady = null;
                var request = new PendingResourceRequest(progress, callerToken);

                await _context.RunAsync(() =>
                {
                    _CheckRequest(callerToken);
                    if (_store.TryGetEntry(key, out entry))
                    {
                        if (entry.State == ResourceState.ReleaseFailed)
                            throw new ResourceLoadException(entry.CleanupError!);

                        if (entry.State == ResourceState.Ready || entry.State == ResourceState.Idle)
                        {
                            if (!entry.IsAssetInvalid && _SafeIsAlive(entry.Asset!))
                            {
                                reference = _AcquireLoadedReference<T>(entry);
                                return;
                            }
                            entry.IsAssetInvalid = true;
                            _store.RemoveIdle(entry);
                            if (entry.HoldCount == 0) _BeginUnload(entry);
                        }
                        if (entry.State != ResourceState.Loading)
                        {
                            previousRemoval = entry.RemovalTask;
                            return;
                        }
                    }
                    else
                    {
                        entry = new ResourceEntry(key, registration, resolved,
                            "op-" + Interlocked.Increment(ref _operationSeed),
                            registration.Policy?.Memory ?? _defaultMemory)
                        {
                            LoadCancellation = new CancellationTokenSource()
                        };
                        _store.AddEntry(entry);
                    }
                    // 登记与入队在同一次提交内完成，保证批次失败不会漏掉已登记的请求。
                    // 这里只取得等待任务，不等待锁；其余请求在领取之前保护同一份结果。
                    entry.PendingRequests.Add(request);
                    lockReady = _resourceLocks.LockAsync(key, callerToken);
                }).ConfigureAwait(false);

                if (reference != null) return reference;
                if (previousRemoval != null)
                {
                    await previousRemoval.WaitWithCancellation(callerToken).ConfigureAwait(false);
                    continue;
                }

                KeyedAsyncLock<ResourceKey>.Releaser? resourceLock = null;
                var startedLoad = false;
                try
                {
                    resourceLock = await lockReady!.ConfigureAwait(false);
                    await _context.RunAsync(() =>
                    {
                        _CheckRequest(callerToken);
                        if (entry.State == ResourceState.Loading && entry.LoadTask == null)
                        {
                            // 锁随真实操作结束而释放；发起者提前取消，不影响仍在等待的请求。
                            var operationLock = resourceLock!;
                            resourceLock = null;
                            startedLoad = true;
                            entry.LoadTask = _RunLoadAsync(entry, operationLock);
                        }
                    }).ConfigureAwait(false);

                    if (startedLoad)
                        await entry.LoadTask!.WaitWithCancellation(callerToken).ConfigureAwait(false);

                    await _context.RunAsync(() =>
                    {
                        _CheckRequest(callerToken);
                        if (entry.LoadError != null)
                        {
                            if (startedLoad || registration.Policy!.FailurePolicy == LoadFailurePolicy.FailWaitingRequests
                                || entry.CleanupError != null)
                                throw new ResourceLoadException(entry.LoadError);
                            // 前一位远端请求已经失败；本请求退出旧记录，重新解析后发起自己的尝试。
                            return;
                        }
                        if (entry.State == ResourceState.ReleaseFailed)
                            throw new ResourceLoadException(entry.CleanupError!);
                        if (entry.State == ResourceState.Ready || entry.State == ResourceState.Idle)
                        {
                            if (!entry.IsAssetInvalid && _SafeIsAlive(entry.Asset!))
                            {
                                reference = _AcquireLoadedReference<T>(entry);
                                return;
                            }
                            entry.IsAssetInvalid = true;
                            if (entry.HoldCount == 0) _BeginUnload(entry);
                        }
                        previousRemoval = entry.RemovalTask;
                    }).ConfigureAwait(false);
                }
                catch (Exception) when (callerToken.IsCancellationRequested)
                {
                    // 本地批次失败与个人取消同时发生时，保留这个请求自己的取消结果。
                    throw new OperationCanceledException(callerToken);
                }
                finally
                {
                    resourceLock?.Dispose();
                    await _context.RunAsync(() => _RemoveWaitingRequest(entry, request)).ConfigureAwait(false);
                }

                if (reference != null) return reference;
                if (previousRemoval != null)
                    await previousRemoval.WaitWithCancellation(callerToken).ConfigureAwait(false);
            }
        }

        private void _CheckRequest(CancellationToken callerToken)
        {
            callerToken.ThrowIfCancellationRequested();
            if (!_isRunning()) throw new ResourceLoadException(_ManagerClosing());
        }

        private long _NextReferenceId() => Interlocked.Increment(ref _leaseSeed);

        private ResourceRef<T> _AcquireLoadedReference<T>(ResourceEntry entry) where T : class
        {
            var asset = entry.Asset!;
            if (!typeof(T).IsInstanceOfType(asset.Value))
                throw new ResourceLoadException(_TypeMismatch(entry, typeof(T), asset.Value));
            _store.AddReference(entry);
            entry.HasBeenReferenced = true;
            return new ResourceRef<T>(this, entry, _NextReferenceId());
        }

        private static bool _HasWaitingRequests(ResourceEntry entry)
        {
            foreach (var request in entry.PendingRequests)
                if (!request.CallerToken.IsCancellationRequested) return true;
            return false;
        }

        private void _RemoveWaitingRequest(ResourceEntry entry, PendingResourceRequest request)
        {
            entry.PendingRequests.Remove(request);
            if (_HasWaitingRequests(entry)) return;
            if (entry.State == ResourceState.Loading)
            {
                entry.State = ResourceState.Draining;
                entry.LoadCancellation?.Cancel();
                if (entry.LoadTask == null)
                {
                    // 全部请求在取得锁之前取消，没有需要等待的后端操作。
                    _DisposeLoadCancellation(entry);
                    _CompleteRemoval(entry);
                }
            }
            else if (entry.State == ResourceState.Ready && entry.HoldCount == 0)
            {
                _ReleaseUnusedResource(entry);
            }
        }

        #endregion

        #region 加载执行与提交 (§6.4)

        /// <summary> 资源锁由操作持有到加载及失败清理结束；调用者只异步等待，不负责提前开锁。 </summary>
        private async Task _RunLoadAsync(ResourceEntry entry, KeyedAsyncLock<ResourceKey>.Releaser resourceLock)
        {
            using (resourceLock)
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
                    result = await entry.Registration.Loader.LoadAsync(
                        entry.Resolved, new LoadProgressReporter(this, entry), entry.LoadToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (entry.LoadToken.IsCancellationRequested)
                {
                    cancelled = true; // 加载器确认取消前已经回退本次取得的资源。
                }
                catch (Exception ex)
                {
                    failure = _ClassifyLoadFailure(ex, entry);
                }
                finally
                {
                    if (acquired) slots!.Release();
                }

                var releaseResult = false;
                await _context.RunAsync(() =>
                {
                    _DisposeLoadCancellation(entry);
                    if (cancelled)
                    {
                        _CompleteRemoval(entry);
                    }
                    else if (failure != null)
                    {
                        _RecordLoadFailure(entry, failure, resourceLock);
                    }
                    else if (result == null)
                    {
                        _RecordLoadFailure(entry, _InvalidResult(entry), resourceLock);
                    }
                    else
                    {
                        entry.Asset = result;
                        entry.EstimatedBytes = result.EstimatedBytes ?? 0;
                        if (result.Value == null || !_SafeIsAlive(result))
                        {
                            entry.LoadError = _InvalidResult(entry);
                            _diagnostics.Report(entry.LoadError);
                            releaseResult = true;
                        }
                        else
                        {
                            releaseResult = entry.State == ResourceState.Draining || !_isRunning()
                                || !_HasWaitingRequests(entry);
                        }
                        // 无接收者或无效的结果在当前锁内清理，不能释放锁后再排一次卸载。
                        entry.State = releaseResult ? ResourceState.Unloading : ResourceState.Ready;
                    }
                    _checkShutdownComplete();
                }).ConfigureAwait(false);

                if (releaseResult) await _UnloadUnderLockAsync(entry, resourceLock).ConfigureAwait(false);
            }
        }

        private LoadError _ClassifyLoadFailure(Exception ex, ResourceEntry entry)
        {
            if (ex is ResourceLoadException rle) return rle.Error;
            return new LoadError(DiagnosticCodes.InternalUnexpected, LoadStage.LoadAsset,
                CleanupStatus.Unknown, ex, _ContextOf(entry));
        }

        private LoadError _InvalidResult(ResourceEntry entry) => new LoadError(
            DiagnosticCodes.LoaderInvalidResult, LoadStage.LoadAsset, CleanupStatus.Complete, null, _ContextOf(entry));

        private Dictionary<string, object> _ContextOf(ResourceEntry entry)
        {
            return new Dictionary<string, object>
            {
                { "loaderId", entry.Key.LoaderId },
                { "key", entry.Key.ToString() },
                { "operationId", entry.OperationId },
            };
        }

        private void _RecordLoadFailure(ResourceEntry entry, LoadError error,
            KeyedAsyncLock<ResourceKey>.Releaser resourceLock)
        {
            entry.LoadError = error;
            _FailWaitingLoads(entry, resourceLock);
            if (error.Cleanup == CleanupStatus.Complete) _CompleteRemoval(entry);
            else _RecordCleanupFailure(entry, error);
            _diagnostics.Report(error);
        }

        private static void _FailWaitingLoads(ResourceEntry entry, KeyedAsyncLock<ResourceKey>.Releaser resourceLock)
        {
            // 在旧记录移除前通知本批次，避免把移除后到达的新请求算进旧失败。
            if (entry.LoadError != null &&
                (entry.Registration.Policy!.FailurePolicy == LoadFailurePolicy.FailWaitingRequests
                 || entry.LoadError.Cleanup != CleanupStatus.Complete))
                resourceLock.FailWaitingRequests(new ResourceLoadException(entry.LoadError));
        }

        #endregion

        #region 持有、空闲与卸载 (§6.5、§9.3)

        internal ResourceRef<T> Retain<T>(ResourceEntry entry) where T : class
        {
            if (!_isRunning())
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
            _store.AddReference(entry);
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
            if (_store.RemoveReference(entry) != 0)
            {
                return;
            }

            _ReleaseUnusedResource(entry);
        }

        /// <summary> 最后引用归还与最后等待者退出，都从这里决定缓存或卸载。 </summary>
        private void _ReleaseUnusedResource(ResourceEntry entry)
        {
            if (_HasWaitingRequests(entry)) return; // 其他请求尚未领取，结果仍需保留。
            var entryPolicy = entry.Policy;
            // 加载器独立内存策略 (R25)：MaxIdleEntries=0 表示该加载器不保留空闲资源；
            // TTL 作用域为条目策略，条目上限/字节预算按系统默认策略执行
            if (!entry.HasBeenReferenced || !_isRunning() || entry.IsAssetInvalid || entryPolicy.IdleLifetime <= TimeSpan.Zero
                || entryPolicy.MaxIdleEntries <= 0 || _defaultMemory.MaxIdleEntries <= 0)
            {
                _BeginUnload(entry);
                return;
            }

            _store.AddIdle(entry, _monotonicNow());
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
            _store.RemoveIdle(entry);
            entry.State = ResourceState.Unloading;
            _ = _RunUnloadAsync(entry);
        }

        private async Task _RunUnloadAsync(ResourceEntry entry)
        {
            using (await _resourceLocks.LockAsync(entry.Key).ConfigureAwait(false))
                await _UnloadUnderLockAsync(entry).ConfigureAwait(false);
        }

        private async Task _UnloadUnderLockAsync(ResourceEntry entry,
            KeyedAsyncLock<ResourceKey>.Releaser? loadLock = null)
        {
            try
            {
                if (entry.Asset != null)
                    await entry.Asset.ReleaseAsync().ConfigureAwait(false);
                await _context.RunAsync(() =>
                {
                    if (loadLock != null) _FailWaitingLoads(entry, loadLock);
                    _CompleteRemoval(entry);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var error = new LoadError(DiagnosticCodes.LifecycleReleaseFailed,
                    LoadStage.ReleaseAsset, CleanupStatus.Incomplete, ex, _ContextOf(entry));
                await _context.RunAsync(() =>
                {
                    if (entry.LoadError != null) entry.LoadError = entry.LoadError.WithRelated(error);
                    if (loadLock != null) _FailWaitingLoads(entry, loadLock);
                    _RecordCleanupFailure(entry, error);
                    _diagnostics.Report(error);
                }).ConfigureAwait(false);
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
            _store.RemoveEntry(entry);
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
                if (now - entry.IdleSince >= entry.Policy.IdleLifetime.TotalSeconds)
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

        #region 关闭：停止加载并释放空闲资源

        internal void BeginClose()
        {
            // 内联上下文中，启动卸载可能同步移除条目。
            foreach (var entry in _store.SnapshotEntries())
            {
                switch (entry.State)
                {
                    case ResourceState.Loading:
                        entry.State = ResourceState.Draining;
                        entry.LoadCancellation?.Cancel();
                        if (entry.LoadTask == null)
                        {
                            _DisposeLoadCancellation(entry);
                            _CompleteRemoval(entry);
                        }
                        break;
                    case ResourceState.Idle:
                        _BeginUnload(entry);
                        break;
                    // Ready 继续为已交付引用服务；Draining/Unloading/ReleaseFailed 保持原路径
                }
            }
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
            private readonly LoadManager _owner;
            private readonly ResourceEntry _entry;

            public LoadProgressReporter(LoadManager owner, ResourceEntry entry)
            {
                _owner = owner;
                _entry = entry;
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
                var list = new IProgress<ResourceProgress>?[_entry.PendingRequests.Count];
                for (var i = 0; i < _entry.PendingRequests.Count; i++)
                {
                    list[i] = _entry.PendingRequests[i].Progress;
                }
                return list;
            }
        }
    }

    /// <summary> 具名加载器注册项：加载器 + 策略快照 (已冻结) + 加载并发槽位 </summary>
    internal sealed class LoaderRegistration
    {
        public readonly string Name;
        public readonly IResourceLoader Loader;
        public readonly LoaderPolicy? Policy;
        public readonly SemaphoreSlim? LoadSlots;

        public LoaderRegistration(string name, IResourceLoader loader, LoaderPolicy? policy)
        {
            Name = name;
            Loader = loader;
            // 省略策略时采用默认四路并发 (R29)；0 表示不限制
            Policy = policy ?? new LoaderPolicy();
            var max = Policy.MaxConcurrentLoads;
            LoadSlots = max > 0 ? new SemaphoreSlim(max, max) : null;
        }
    }
}
