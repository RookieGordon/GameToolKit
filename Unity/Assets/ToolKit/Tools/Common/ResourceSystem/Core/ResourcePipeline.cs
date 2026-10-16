/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 资源加载、交付、归还与卸载的唯一主流程 (设计稿 §3)。
 *                一条流水线：解析 → 复用或合并同次加载 → 交付独立引用 → 归还后等待业务卸载。
 *                本文件内的类型各守职责：ResourcePipeline 推进流程并保存产物复用索引，
 *                ResourceRecord 保存一次加载的事实与等待者，ResourceEntry 保存产物与持有计数。
 *                状态只在状态串行域内改变；提交与外部调用分离，外部代码前显式让出执行权。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    internal sealed class ResourcePipeline
    {
        private readonly IExecutionContext _context;
        private readonly IResourceDiagnostics _diagnostics;
        private readonly IErrorMapper _errorMapper;
        private readonly Func<bool> _isDisposed;
        private readonly Func<double> _monotonicNow;
        // 复用索引：一个身份只有一个可复用产物。
        private readonly Dictionary<ResourceKey, ResourceEntry> _current =
            new Dictionary<ResourceKey, ResourceEntry>();
        // 仍由引用或业务卸载持有的具体产物；撤下复用索引后依然存在于这里。
        private readonly HashSet<ResourceEntry> _entries = new HashSet<ResourceEntry>();
        // 在途记录：同键合并的等待者与"正在执行一次加载"的事实都保存在这里。
        private readonly Dictionary<ResourceKey, ResourceRecord> _inflight =
            new Dictionary<ResourceKey, ResourceRecord>();
        private readonly Dictionary<ResourceSource, LoaderRegistration> _loaders =
            new Dictionary<ResourceSource, LoaderRegistration>();
        private readonly List<DirtyResource> _dirtyResources = new List<DirtyResource>();
        private readonly object _loaderGate = new object();
        private readonly InstancePool _pool;
        private readonly ResourceSystemOptions _options;
        private long _operationSeed;
        private long _leaseSeed;
        private int _disposed;

        internal ResourcePipeline(
            IExecutionContext context,
            ResourceSystemOptions options,
            IResourceDiagnostics diagnostics,
            IErrorMapper errorMapper,
            Func<double> monotonicNow,
            Func<bool> isDisposed)
        {
            _context = context;
            _options = options;
            _diagnostics = diagnostics;
            _errorMapper = errorMapper;
            _monotonicNow = monotonicNow;
            _isDisposed = isDisposed;
            _pool = new InstancePool(this);
        }

        internal IExecutionContext Context => _context;

        internal IResourceDiagnostics Diagnostics => _diagnostics;

        internal IErrorMapper ErrorMapper => _errorMapper;

        internal Func<double> MonotonicNow => _monotonicNow;

        /// <summary> 单次业务调用的默认时间预算；租用与加载共用同一份配置 </summary>
        internal TimeSpan? RequestTimeout => _options.RequestTimeout;

        internal Func<bool> IsDisposed => _isDisposed;

        internal InstancePool Pool => _pool;

        #region 加载器与工厂注册

        internal void RegisterLoader(ResourceSource source, IResourceLoader loader, LoaderPolicy? policy)
        {
            if (loader == null) throw new ArgumentNullException(nameof(loader));
            ValidateSource(source);
            lock (_loaderGate)
            {
                if (_loaders.ContainsKey(source))
                    throw new InvalidOperationException($"资源来源已注册加载器: {source}；不能重复注册");
                _loaders.Add(source, _CreateRegistration(source, loader, policy));
            }
        }

        internal void ReplaceLoader(ResourceSource source, IResourceLoader loader, LoaderPolicy? policy)
        {
            if (loader == null) throw new ArgumentNullException(nameof(loader));
            ValidateSource(source);
            lock (_loaderGate)
            {
                if (!_loaders.ContainsKey(source))
                    throw new InvalidOperationException($"资源来源尚未注册加载器: {source}，请先注册");
                var replacement = _CreateRegistration(source, loader, policy);
                _loaders[source].Concurrency.Dispose();
                _loaders[source] = replacement;
            }
        }

        private static LoaderRegistration _CreateRegistration(
            ResourceSource source, IResourceLoader loader, LoaderPolicy? policy)
        {
            var snapshot = policy != null ? LoaderPolicy.Clone(policy) : new LoaderPolicy();
            snapshot.Validate();
            return new LoaderRegistration(source, loader, snapshot);
        }

        /// <summary> 来源只允许四种显式取值；未指定或越界直接拒绝 </summary>
        internal static void ValidateSource(ResourceSource source)
        {
            if (source < ResourceSource.Resources || source > ResourceSource.Remote)
                throw new ArgumentOutOfRangeException(nameof(source), source, "必须指定有效的资源来源");
        }

        /// <summary> 路由只读取注册表，不调用引擎 API </summary>
        internal LoaderRegistration Route(ResourceSource source)
        {
            ValidateSource(source);
            lock (_loaderGate)
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

        #endregion

        #region 业务入口：取得引用

        /// <summary>
        /// 按业务地址取得引用。旧记录退出后重新解析地址，允许加载器映射到新的资源版本。
        /// </summary>
        internal Task<ResourceRef<T>> LoadAsync<T>(
            ResourceSource source, string address, RequestOptions? options, CancellationToken callerToken)
            where T : class
        {
            var registration = Route(source);
            var request = new ResourceRequest(source, address, typeof(T), options?.Parameters);
            return _AcquireAsync<T>(
                registration,
                token => registration.Loader.ResolveAsync(request, token),
                address, options, callerToken);
        }

        /// <summary>
        /// 实例池已选定原型版本；等待旧记录结束时仍使用同一份描述，不重新解析地址。
        /// </summary>
        internal Task<ResourceRef<object>> AcquireResolvedAsync(
            LoaderRegistration registration, ResolvedResource resolved, CancellationToken callerToken)
        {
            return _AcquireAsync<object>(
                registration, _ => Task.FromResult(resolved), resolved.LocalKey, null, callerToken);
        }

        /// <summary>
        /// 主流程：解析 → 等待已放弃或正在卸载的旧记录 → 命中产物 → 合并到在途记录或新建记录 → 等本轮结果。
        /// 收到"本轮无产物"且本请求不是本轮归属者时重新解析，这就是接续尝试的落点。
        /// </summary>
        private async Task<ResourceRef<T>> _AcquireAsync<T>(
            LoaderRegistration registration,
            Func<CancellationToken, Task<ResolvedResource>> resolve,
            string address,
            RequestOptions? options,
            CancellationToken callerToken) where T : class
        {
            using (var budget = _StartBudget(options, callerToken))
            {
                var waitToken = budget.Token;
                while (true)
                {
                    waitToken.ThrowIfCancellationRequested();
                    _CheckDisposed();

                    var resolved = await resolve(waitToken).ConfigureAwait(false);
                    _ValidateResolved(resolved, address);
                    var key = new ResourceKey(registration.Source, resolved.LocalKey);

                    ResourceRecord? record = null;
                    Waiter<T>? waiter = null;
                    Task? previous = null;
                    var startsLoad = false;

                    await _context.RunAsync(() =>
                    {
                        waitToken.ThrowIfCancellationRequested();
                        _CheckDisposed();

                        // 正在卸载的当前记录：先等它真实结束，再决定复用或新加载。
                        if (_TryGetUnloadingProduct(key, out var unloading))
                        {
                            previous = unloading.RemovalTask;
                            return;
                        }

                        if (_TryGetLiveProduct(key, out var entry))
                        {
                            // 命中：不排队、不建记录，现场建立持有。
                            waiter = Waiter<T>.Granted(this, entry, waitToken, options?.Progress);
                            return;
                        }

                        if (_inflight.TryGetValue(key, out var existing))
                        {
                            if (existing.Attach<T>(waitToken, options?.Progress) is Waiter<T> attached)
                            {
                                waiter = attached;
                                record = existing;
                                return;
                            }
                            // 本轮已经终结：只等它结束收尾，不接收它的结果。
                            previous = existing.Finished;
                            return;
                        }

                        // 新建记录，本请求是首个接收者。
                        var created = new ResourceRecord(this, key, resolved, registration,
                            "op-" + Interlocked.Increment(ref _operationSeed));
                        waiter = created.Attach<T>(waitToken, options?.Progress);
                        record = created;
                        _inflight.Add(key, created);
                        startsLoad = true;
                    }).ConfigureAwait(false);

                    if (waiter == null)
                    {
                        await previous!.WaitWithCancellation(waitToken).ConfigureAwait(false);
                        continue;
                    }

                    // 命中复用：现场已经建立持有，不排队也不等任何通知。
                    if (record == null) return waiter.DeliveredReference!;

                    if (startsLoad)
                    {
                        // 登记已经结束，启动前显式让出执行权：外部代码不得嵌在登记栈内。
                        await Task.Yield();
                        _StartExecution(record);
                    }

                    var reference = await _AwaitWaiterAsync(record, waiter, budget).ConfigureAwait(false);
                    if (reference != null) return reference;
                    // 本轮无产物且本请求不是归属者：重新解析并参加下一轮。
                }
            }
        }

        /// <summary>
        /// 等待本次的完成通知；"本轮无产物"返回 null 交由调用方接续。
        /// 先登记取消回调再等待：保证"完成通知一旦置位即所有权交接"，
        /// 取消此后只可能观察到已经授予的引用，不会夺走它。
        /// </summary>
        private async Task<ResourceRef<T>?> _AwaitWaiterAsync<T>(
            ResourceRecord record, Waiter<T> waiter, Budget budget) where T : class
        {
            using (budget.Token.Register(() => _ = _CancelWaiterAsync(record, waiter, budget)))
            {
                await waiter.Completion.ConfigureAwait(false);
            }
            waiter.MarkReleased();

            // 个人取消优先：完成通知与取消同时发生时，业务看到的是自己的取消。
            if (waiter.WasCanceled || waiter.Token.IsCancellationRequested) waiter.Cancel(waiter.TimedOut);

            switch (waiter.Result)
            {
                case WaitResult.Granted:
                    return waiter.DeliveredReference!;
                case WaitResult.Retry:
                    return null;
                case WaitResult.Canceled:
                    if (waiter.TimedOut) throw new ResourceLoadException(waiter.TimeoutError());
                    throw new OperationCanceledException(waiter.Token);
                default:
                    if (waiter.TimedOut) throw new ResourceLoadException(waiter.TimeoutError());
                    throw new ResourceLoadException(waiter.Failure ?? _Unexpected(record.Key));
            }
        }

        /// <summary>
        /// 交付提交之前取消本次等待：把它移出接收者集合；全部退出时在同一次提交内关闭准入并放弃这条记录。
        /// 时间预算到期只结束本请求，不取消共享后端；调用者主动取消同一轮请求时取消后端。
        /// 交付提交之后到达的取消是空转 —— 引用已归属调用者。
        /// </summary>
        private async Task _CancelWaiterAsync(ResourceRecord record, Waiter waiter, Budget budget)
        {
            var timedOut = budget.TimedOut;
            var abandon = false;
            await _context.RunAsync(() =>
            {
                if (!record.Remove(waiter))
                {
                    // 已经不在接收者集合：要么已交付，要么已经结算过，取消不再改变结果。
                    if (!timedOut) return;
                    waiter.Cancel(true);
                    return;
                }
                waiter.Cancel(timedOut);
                // 判空与放弃准入必须同一次提交：否则两次提交之间加入的等待者会被误伤。
                if (record.WaitingCount == 0 && !record.Finished.IsCompleted)
                {
                    record.MarkAbandoned();
                    abandon = true;
                }
            }).ConfigureAwait(false);
            // 提交外只执行"已决定的动作"：促使后端尽早退出，不做结果分配。
            if (abandon) record.CancelBackend();
        }

        /// <summary>
        /// 取得这条记录的执行责任。一条记录只允许一个执行者：
        /// 登记时的首个接收者与放弃路径都通过它竞争，输的一方不再启动第二个执行循环。
        /// </summary>
        private void _StartExecution(ResourceRecord record)
        {
            if (!record.TryClaimExecution()) return;
            _ = _RunLoadAsync(record);
        }

        /// <summary>
        /// 加载内容 → 最终资产。文件类来源实现 <see cref="IConvertingLoader"/>，由它用注册的转换器完成转换；
        /// 不需要转换的来源（引擎共享对象）直接把内容当作资产，并保留其存活语义。
        /// 转换差异标识由 ResolvedResource 带回，编排层不解释内容。
        /// <b>不变量</b>：内容一旦取到就必须有主——转换没交出资产时，在这里释放内容，
        /// 不能把"已取到但没交出去"的东西留给一个还没建立的产物记录。
        /// </summary>
        private static async Task<LoadedAsset> _ConvertAsync(
            IResourceLoader loader, LoadedContent content, ResolvedResource resolved, CancellationToken operationToken)
        {
            try
            {
                if (loader is IConvertingLoader converting)
                {
                    return await converting.ConvertAsync(content, resolved, operationToken).ConfigureAwait(false);
                }
                return LoadedAsset.FromContent(content, () => content.Value != null);
            }
            catch (Exception)
            {
                await content.ReleaseAsync().ConfigureAwait(false);
                throw;
            }
        }

        private void _CheckDisposed()
        {
            if (_isDisposed()) throw new ResourceLoadException(_ManagerDisposed());
        }

        #endregion

        #region 执行一次加载：取额度 → 批准 → 调用后端 → 提交交付或收尾

        /// <summary>
        /// 执行一次加载。出口共用同一段终结：成功交付、本轮无产物、迟到产物收尾。
        /// 每个出口恰好终结记录一次、恰好摘表一次；额度要么从未取得、要么恰好归还一次。
        /// </summary>
        private async Task _RunLoadAsync(ResourceRecord record)
        {
            LoadedAsset? product = null;
            LoadedContent? pendingContent = null;
            LoadError? failure = null;
            var delivered = false;
            var needsCleanup = false;
            var budgetHeld = false;

            try
            {
                // ① 取并发额度：令牌只负责"尽早退出"，不承担批准职责。
                await record.Registration.Concurrency.WaitAsync(record.OperationToken).ConfigureAwait(false);
                budgetHeld = true;

                // ② 取到额度后在串行提交内复查：已放弃、没有接收者或管理器已退出都不批准启动。
                var approved = false;
                await _context.RunAsync(() =>
                {
                    approved = !record.Abandoned && record.WaitingCount > 0 && !_isDisposed();
                    if (approved) record.ApproveStart();
                }).ConfigureAwait(false);

                if (approved)
                {
                    // ③ 批准提交是后端第一行之前最后一次状态修改，中间没有用户回调。
                    // 加载器只交内容；转换由加载器自己的转换步骤完成，未交付前由 LoadedAsset 承载释放责任。
                    var content = await record.Registration.Loader
                        .LoadAsync(record.Resolved, record, record.OperationToken)
                        .ConfigureAwait(false);
                    pendingContent = content;
                    product = await _ConvertAsync(
                        record.Registration.Loader, content, record.Resolved, record.OperationToken)
                        .ConfigureAwait(false);
                    pendingContent = null;

                    await _context.RunAsync(() =>
                    {
                        // 记录已被放弃或退出：即使内容已经取到、转换已经完成，也不交付给任何人。
                        // 迟到的产物由本轮收尾释放（finally 里统一处理），不留下无人负责的对象。
                        if (record.Abandoned)
                        {
                            needsCleanup = true;
                            return;
                        }
                        if (!IsAssetAlive(product))
                        {
                            // 后端交出的产物无效：本轮失败，产物由原记录收尾。
                            failure = _InvalidResult(record);
                            needsCleanup = true;
                            return;
                        }
                        delivered = _Deliver(record, product!);
                        needsCleanup = !delivered;
                    }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (record.OperationToken.IsCancellationRequested)
            {
                // 已放弃或退出的记录：没有产物，走统一终结。
            }
            catch (Exception error)
            {
                failure = error is ResourceLoadException load ? load.Error : new LoadError(
                    DiagnosticCodes.InternalUnexpected, LoadStage.LoadAsset, CleanupStatus.Unknown, error,
                    record.DiagnosticContext());
            }
            finally
            {
                // 提交阶段自身抛错 (如用户工厂回调) 也不能漏掉产物。
                if (product != null && !delivered) needsCleanup = true;
                else if (pendingContent != null) needsCleanup = true;

                if (needsCleanup)
                {
                    // 只取到内容、转换没交出资产：内容仍须释放，否则留下无人负责的已取内容。
                    if (product == null && pendingContent != null)
                    {
                        try
                        {
                            await pendingContent.ReleaseAsync().ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            failure = failure ?? new LoadError(
                                DiagnosticCodes.LifecycleReleaseFailed, LoadStage.ReleaseAsset,
                                CleanupStatus.Incomplete, error, record.DiagnosticContext());
                        }
                    }
                    var cleanup = await _CleanupAsync(record, product).ConfigureAwait(false);
                    if (cleanup.EntryRemoved) product = null; // 具体条目已收口，终结阶段不再接管
                    if (cleanup.Error != null)
                        failure = failure == null ? cleanup.Error : failure.WithRelated(cleanup.Error);
                }

                // 唯一终结与摘除点：与本轮收尾完成同一次提交，中间没有 await。
                await _context.RunAsync(() => _Terminate(record, failure, product)).ConfigureAwait(false);

                if (budgetHeld) record.Registration.Concurrency.Release();
            }
        }

        /// <summary>
        /// 交付提交：关闭准入、发布产物、为**本轮的接收者**建立独立持有、一次性完成通知。
        /// 只交付本轮名册内的等待者：晚到的请求属于下一轮，不会被交付错过而悬挂。
        /// </summary>
        private bool _Deliver(ResourceRecord record, LoadedAsset product)
        {
            record.CloseAdmission(); // 这是本轮最后一次接受新接收者。
            var entry = _RegisterProduct(product, record.Key);
            record.Bind(entry);

            var roster = record.CollectDeliveries();
            var delivered = 0;
            foreach (var waiter in roster)
            {
                if (waiter.TryGrant(entry)) delivered++;
            }

            if (delivered == 0)
            {
                // 无人接收：不能留下指向待释放产物的复用项。
                _DetachProduct(entry);
                return false;
            }

            foreach (var waiter in roster) waiter.Deliver();
            return true;
        }

        /// <summary>
        /// 未交付产物的收尾：与业务卸载、退出释放共用同一份"取得释放责任 → 执行 → 登记结果"。
        /// 取得不到责任说明已有责任方在释放这份产物，沿用它的完成通知，不启动第二次释放。
        /// </summary>
        private async Task<CleanupResult> _CleanupAsync(ResourceRecord record, LoadedAsset? product)
        {
            var entry = record.Entry;
            // 未登记：产物还没有进入所有权集合，由本轮操作直接释放 (登记后才有条目责任可取得)。
            if (entry == null || product == null)
            {
                if (product == null) return new CleanupResult(null, false);
                try
                {
                    await product.ReleaseAsync().ConfigureAwait(false);
                    return new CleanupResult(null, false);
                }
                catch (Exception error)
                {
                    return new CleanupResult(_RecordCleanupFailure(record, product, error), false);
                }
            }
            if (!entry.TryBeginUnload())
            {
                await entry.RemovalTask.ConfigureAwait(false);
                return new CleanupResult(null, true);
            }

            LoadError? failure = null;
            try
            {
                await entry.Asset.ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure = _RecordCleanupFailure(record, entry.Asset, error);
            }
            // 无论释放成功与否都在终结提交内收口：失败时残留责任已明确移交，不让退出重复释放它。
            await _context.RunAsync(() =>
            {
                _RemoveProduct(entry);
                entry.CompleteRemoval(failure);
            }).ConfigureAwait(false);
            return new CleanupResult(failure, true);
        }

        /// <summary>
        /// 释放未确认结束时登记一次失败：残留对象与依赖交给诊断与脏集合，
        /// 记录本身照常退出，同键不被永久封锁。
        /// </summary>
        private LoadError _RecordCleanupFailure(ResourceRecord record, LoadedAsset product, Exception error)
        {
            var failure = error is ResourceLoadException failed
                ? failed.Error
                : new LoadError(DiagnosticCodes.LifecycleReleaseFailed, LoadStage.ReleaseAsset,
                    CleanupStatus.Incomplete, error, record.DiagnosticContext());
            var retained = error is ResourceLoadException remainder && remainder.CleanupRemainders.Count > 0
                ? remainder.CleanupRemainders
                : _RetainedProduct(record, product);
            _AddDirty(new DirtyResource(record.Key, failure, retained));
            _diagnostics.Report(failure);
            return failure;
        }

        /// <summary> 收尾结果：错误 + 具体条目是否已经收口 </summary>
        private readonly struct CleanupResult
        {
            internal CleanupResult(LoadError? error, bool entryRemoved)
            {
                Error = error;
                EntryRemoved = entryRemoved;
            }

            internal LoadError? Error { get; }

            internal bool EntryRemoved { get; }
        }

        /// <summary> 本次失败留下的具体对象；产物只在串行域内读取 </summary>
        private IReadOnlyList<object> _RetainedProduct(ResourceRecord record, LoadedAsset product)
        {
            try
            {
                return new object[] { _context.Invoke(() => product.Value) };
            }
            catch (Exception)
            {
                // 取不到产物对象时，仍以记录本身作为可排查的残留线索。
                return new object[] { record };
            }
        }

        /// <summary>
        /// 唯一终结与摘除点。摘表发生在本轮收尾之后且与终结同一提交，
        /// 因此"在途表里查不到记录 ⟹ 这次加载的整个收尾已经完成"。
        /// 释放失败不永久封锁 key：残留移交后照常移除记录。
        /// </summary>
        /// <param name="product">
        /// 仍需收尾的产物；null 表示该条目的收尾责任已在清理阶段结束 (或本轮没有产物)。
        /// </param>
        private void _Terminate(ResourceRecord record, LoadError? failure, LoadedAsset? product)
        {
            var entry = record.Entry;
            if (entry != null && product != null && entry.HoldCount == 0)
            {
                _DetachProduct(entry);
            }
            _inflight.Remove(record.Key);
            // 先终结再摘表语义上同时发生；Complete 会清空等待者，之后到达的请求只看 Finished。
            record.Complete(failure);
            record.DisposeCancellation();
            if (failure != null) _diagnostics.Report(failure);
        }

        #endregion

        #region 业务卸载：选定范围 → 复查持有 → 释放 → 汇总结果

        /// <summary>
        /// 由业务决定何时、清理哪部分。选择器在状态提交之外执行 (它可能重新取得资源)，
        /// 执行前必须以该记录的当前持有为准复查一次。取消只停止调用者等待，已开始的释放仍独立完成。
        /// </summary>
        internal async Task<ResourceUnloadResult> UnloadUnusedAsync(
            Predicate<ResourceRow>? select, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await _context.RunAsync(_SnapshotForSelection).ConfigureAwait(false);

            var selected = new List<ResourceEntry>();
            foreach (var item in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (select == null || select(item.Row)) selected.Add(item.Entry);
            }

            var releases = new List<Task>();
            var pending = 0;
            var skipped = 0;
            var joined = new List<Task>();
            var failures = new List<DirtyResource>();
            await _context.RunAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in selected)
                {
                    if (!_entries.Contains(entry)) continue;
                    // 快照之后可能已被重新持有，以这份具体记录的当前状态为准。
                    if (entry.HoldCount != 0)
                    {
                        skipped++;
                        continue;
                    }
                    // 已经在释放中的沿用其完成通知；取得责任的同时就启动释放，不留"已标记但没人释放"的缝隙。
                    if (!entry.TryBeginUnload())
                    {
                        joined.Add(entry.RemovalTask);
                        continue;
                    }
                    pending++;
                    releases.Add(_RunUnloadAsync(entry, failures, claimed: true));
                }
            }).ConfigureAwait(false);

            // 取消只停止调用者等待；已开始的释放仍独立完成并移交残留。
            foreach (var release in releases)
            {
                await release.WaitWithCancellation(cancellationToken).ConfigureAwait(false);
            }
            foreach (var waiting in joined)
            {
                await waiting.WaitWithCancellation(cancellationToken).ConfigureAwait(false);
            }

            lock (failures)
            {
                return new ResourceUnloadResult(pending, skipped, new List<DirtyResource>(failures));
            }
        }

        /// <summary>
        /// 启动一次释放。调用方要么已经取得释放责任 (claimed)，
        /// 要么本方法自己取得；取得失败时沿用已完成责任方的完成通知，绝不重复启动 ReleaseAsync。
        /// </summary>
        private async Task _RunUnloadAsync(ResourceEntry entry, List<DirtyResource>? failures, bool claimed = false)
        {
            if (!claimed && !entry.TryBeginUnload())
            {
                await entry.RemovalTask.ConfigureAwait(false);
                return;
            }

            LoadError? failure = null;
            try
            {
                await entry.Asset.ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure = error is ResourceLoadException failed
                    ? failed.Error
                    : new LoadError(DiagnosticCodes.LifecycleReleaseFailed, LoadStage.ReleaseAsset,
                        CleanupStatus.Incomplete, error, entry.DiagnosticContext());
                var retained = error is ResourceLoadException remainder && remainder.CleanupRemainders.Count > 0
                    ? remainder.CleanupRemainders
                    : _RetainedAsset(entry);
                // 同一份残留同时进入全局脏集合与本次业务结果：取消等待后也查得到。
                var dirty = new DirtyResource(entry.Key, failure, retained);
                _AddDirty(dirty);
                if (failures != null) lock (failures) failures.Add(dirty);
            }
            finally
            {
                await _context.RunAsync(() =>
                {
                    // 释放失败也已移交残留：记录照常退出，同键可以重新加载。
                    _RemoveProduct(entry);
                    entry.CompleteRemoval(failure);
                    if (failure != null) _diagnostics.Report(failure);
                }).ConfigureAwait(false);
            }
        }

        /// <summary> 释放失败留下的具体对象；产物只在串行域内读取 </summary>
        private IReadOnlyList<object> _RetainedAsset(ResourceEntry entry)
        {
            try
            {
                return new object[] { _context.Invoke(() => entry.Asset.Value) };
            }
            catch (Exception)
            {
                return new object[] { entry };
            }
        }

        private void _AddDirty(DirtyResource dirty)
        {
            lock (_dirtyResources) _dirtyResources.Add(dirty);
        }

        internal List<DirtyResource> SnapshotDirtyResources()
        {
            lock (_dirtyResources) return new List<DirtyResource>(_dirtyResources);
        }

        /// <summary> 诊断查询：复用索引中的当前项按真实状态投影，在途记录报告等待人数 </summary>
        internal List<ResourceRow> SnapshotRows()
        {
            var rows = new List<ResourceRow>(_current.Count + _inflight.Count);
            foreach (var entry in _current.Values)
            {
                rows.Add(new ResourceRow(entry.Key, _StateOf(entry), entry.HoldCount, 0));
            }
            foreach (var record in _inflight.Values)
            {
                rows.Add(new ResourceRow(record.Key,
                    record.Abandoned ? ResourceState.Draining : ResourceState.Loading,
                    0, record.WaitingCount));
            }
            return rows;
        }

        internal void Tick() => _pool.Tick();

        #endregion

        #region 产物复用索引：登记、撤下与查询

        /// <summary> 登记一次新产物并加入复用索引 </summary>
        private ResourceEntry _RegisterProduct(LoadedAsset asset, ResourceKey key)
        {
            var entry = new ResourceEntry(this, key, asset, ++_leaseSeed);
            _current[key] = entry;
            _entries.Add(entry);
            return entry;
        }

        private void _RemoveProduct(ResourceEntry entry)
        {
            _DetachProduct(entry);
            _entries.Remove(entry);
        }

        /// <summary> 只取消按键复用；仍保留具体产物及其引用，等待业务卸载 </summary>
        private void _DetachProduct(ResourceEntry entry)
        {
            if (_current.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                _current.Remove(entry.Key);
            }
        }

        /// <summary>
        /// 取出可按身份复用的产物；正在卸载或已经不存活都不算命中。
        /// 查询存活失败只撤下复用索引，不触发任何释放。
        /// </summary>
        private bool _TryGetLiveProduct(ResourceKey key, out ResourceEntry entry)
        {
            entry = null!;
            if (!_current.TryGetValue(key, out var candidate)) return false;
            if (candidate.IsUnloading) return false;
            if (!candidate.IsAlive)
            {
                _DetachProduct(candidate);
                return false;
            }
            entry = candidate;
            return true;
        }

        /// <summary> 取出正在释放中的当前记录；新请求要等它真实结束再决定复用或新加载 </summary>
        private bool _TryGetUnloadingProduct(ResourceKey key, out ResourceEntry entry)
        {
            entry = null!;
            if (!_current.TryGetValue(key, out var candidate)) return false;
            if (!candidate.IsUnloading) return false;
            entry = candidate;
            return true;
        }

        private static ResourceState _StateOf(ResourceEntry entry)
        {
            if (entry.IsUnloading) return ResourceState.Unloading;
            return entry.HoldCount > 0 ? ResourceState.Ready : ResourceState.Idle;
        }

        /// <summary>
        /// 卸载选择用的快照：行只携带业务可见的事实，记录单独交出，
        /// 避免在诊断类型上挂内部对象。
        /// </summary>
        private List<(ResourceRow Row, ResourceEntry Entry)> _SnapshotForSelection()
        {
            var rows = new List<(ResourceRow, ResourceEntry)>(_current.Count);
            foreach (var entry in _current.Values)
            {
                rows.Add((new ResourceRow(entry.Key, _StateOf(entry), entry.HoldCount, 0), entry));
            }
            return rows;
        }

        /// <summary> 查询具体产物的有效性；异常不等于确认已销毁 </summary>
        internal static bool IsAssetAlive(LoadedAsset asset)
        {
            try { return asset.Value != null && asset.IsAlive(); }
            catch { return false; }
        }

        #endregion

        #region 退出

        /// <summary>
        /// 停止使用：先通知全部等待者，再释放所有已登记产物。
        /// 退出释放不等待业务归还引用，也不以引用归零为释放条件。
        /// </summary>
        internal async Task DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            var records = new List<ResourceRecord>();
            await _context.RunAsync(() =>
            {
                records.AddRange(_inflight.Values);
                _inflight.Clear();
            }).ConfigureAwait(false);

            // 先通知全部等待者，再取消后端：退出不得让业务调用悬挂。
            foreach (var record in records)
            {
                await _context.RunAsync(() =>
                {
                    record.CloseAdmission();
                    record.Complete(_ManagerDisposed());
                }).ConfigureAwait(false);
                record.CancelBackend();
                record.DisposeCancellation();
            }

            // 已登记产物：已经在释放中的沿用其完成通知，只为尚未开始的取得释放责任，
            // 保证同一产物的 ReleaseAsync 至多启动一次。
            var releases = new List<Task>();
            await _context.RunAsync(() =>
            {
                foreach (var entry in new List<ResourceEntry>(_entries))
                {
                    // 退出不等待业务归还引用：未开始的直接取得责任，已在释放中的沿用其完成通知。
                    if (entry.TryBeginUnload(force: true))
                    {
                        releases.Add(_RunUnloadAsync(entry, null, claimed: true));
                    }
                    else
                    {
                        releases.Add(entry.RemovalTask);
                    }
                }
            }).ConfigureAwait(false);
            foreach (var release in releases) await release.ConfigureAwait(false);
            await _pool.DisposeAsync().ConfigureAwait(false);
        }

        #endregion

        #region 单次调用的时间预算

        /// <summary> 单次调用的时间预算；超时只结束本请求的等待，不取消共享后端 </summary>
        private sealed class Budget : IDisposable
        {
            private readonly CancellationTokenSource? _source;
            private readonly Func<double> _now;
            private readonly double _deadline;

            internal Budget(CancellationTokenSource? source, CancellationToken token, Func<double> now, double deadline)
            {
                _source = source;
                Token = token;
                _now = now;
                _deadline = deadline;
            }

            internal CancellationToken Token { get; }

            /// <summary> 是否因时间预算到期而取消 </summary>
            internal bool TimedOut =>
                _deadline > 0 && !double.IsPositiveInfinity(_deadline) && _now() >= _deadline;

            public void Dispose() => _source?.Dispose();
        }

        private Budget _StartBudget(RequestOptions? options, CancellationToken callerToken)
        {
            var timeout = options?.Timeout ?? _options.RequestTimeout;
            if (timeout is not TimeSpan limit)
            {
                return new Budget(null, callerToken, _monotonicNow, double.PositiveInfinity);
            }
            var source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            source.CancelAfter(limit);
            return new Budget(source, source.Token, _monotonicNow, _monotonicNow() + limit.TotalSeconds);
        }

        private LoadError _Unexpected(ResourceKey key) => new LoadError(
            DiagnosticCodes.InternalUnexpected, LoadStage.WaitForLoad, CleanupStatus.Unknown, null,
            new Dictionary<string, object> { { "key", key.ToString() } });

        private static LoadError _InvalidResult(ResourceRecord record) => new LoadError(
            DiagnosticCodes.LoaderInvalidResult, LoadStage.LoadAsset, CleanupStatus.Complete, null,
            record.DiagnosticContext());

        private static LoadError _ManagerDisposed() => new LoadError(
            DiagnosticCodes.LifecycleManagerDisposed, LoadStage.Dispose, CleanupStatus.Complete);

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

        #endregion
    }

    /// <summary> 一个来源的加载器、策略快照与并发额度 </summary>
    internal sealed class LoaderRegistration
    {
        internal readonly ResourceSource Source;
        internal readonly IResourceLoader Loader;
        internal readonly LoaderPolicy Policy;
        internal readonly SemaphoreSlim Concurrency;

        internal LoaderRegistration(ResourceSource source, IResourceLoader loader, LoaderPolicy policy)
        {
            Source = source;
            Loader = loader;
            Policy = policy;
            Concurrency = policy.MaxConcurrentLoads > 0
                ? new SemaphoreSlim(policy.MaxConcurrentLoads, policy.MaxConcurrentLoads)
                : new SemaphoreSlim(int.MaxValue, int.MaxValue);
        }
    }

    /// <summary> 本次等待的终态；由交付提交或终结提交恰好置位一次 </summary>
    internal enum WaitResult
    {
        /// <summary> 尚未置位：还没有为本次调用分配任何结果 </summary>
        Unset,
        Granted,
        Retry,
        Failed,
        Canceled,
    }

    /// <summary>
    /// 一次业务调用的等待者：自己的完成通知、自己的目标类型与进度。
    /// 完成通知一旦置位即所有权交接，个人取消此后不得短路该通知；
    /// 尚在等待时取消则按取消完成，不参加交付分配。
    /// </summary>
    internal abstract class Waiter
    {
        protected Waiter(ResourceKey key, CancellationToken token, IProgress<ResourceProgress>? progress)
        {
            Key = key;
            Token = token;
            Progress = progress;
        }

        private readonly TaskCompletionSource<bool> _completion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary> 在记录内按登记顺序递增；用于确定"谁先发起这次加载" </summary>
        internal long Sequence { get; set; }

        internal ResourceKey Key { get; }

        internal CancellationToken Token { get; }

        internal IProgress<ResourceProgress>? Progress { get; }

        internal WaitResult Result { get; set; }

        internal LoadError? Failure { get; set; }

        internal bool TimedOut { get; private set; }

        /// <summary> 是否在结果分配之前取消；与归属者判定共同决定终态 </summary>
        internal bool WasCanceled { get; private set; }

        internal bool Released { get; private set; }
        internal Task Completion => _completion.Task;

        internal void MarkReleased() => Released = true;

        /// <summary> 交付提交调用：为本次调用建立独立持有；类型不符只让本项失败 </summary>
        internal abstract bool TryGrant(ResourceEntry entry);

        internal void Deliver() => _completion.TrySetResult(true);

        internal void Fail(LoadError error)
        {
            Result = WaitResult.Failed;
            Failure = error;
            _completion.TrySetResult(true);
        }

        internal void Retry()
        {
            Result = WaitResult.Retry;
            _completion.TrySetResult(true);
        }

        /// <summary>
        /// 结果分配之前取消：置位终态并结束等待；由流程在串行域内调用。
        /// 已经授予的引用保持授予：取消不得让交付过的调用者失去引用。
        /// </summary>
        /// <summary>
        /// 结果分配之前取消：置位终态并结束等待；由流程在串行域内调用。
        /// 已经置位的终态（含已授予的引用）不再改动：取消不得夺走已交付的引用。
        /// </summary>
        internal void Cancel(bool timedOut)
        {
            if (Result != WaitResult.Unset) return;
            WasCanceled = true;
            Result = WaitResult.Canceled;
            TimedOut = timedOut;
            _completion.TrySetResult(true);
        }

        /// <summary> 是否已经为本次调用分配了引用；已分配即所有权已经交接 </summary>
        internal abstract bool HasDeliveredReference { get; }

        /// <summary> 是否已经承担过一轮失败；承担过就不再被推为下一轮归属者 </summary>
        internal bool HasBorneFailure { get; private set; }

        internal void MarkBorneFailure() => HasBorneFailure = true;

        internal LoadError TimeoutError() => new LoadError(
            DiagnosticCodes.RequestTimeout, LoadStage.WaitForLoad, CleanupStatus.Complete, null,
            new Dictionary<string, object> { { "key", Key.ToString() } });
    }

    internal sealed class Waiter<T> : Waiter where T : class
    {
        private readonly ResourcePipeline _pipeline;

        internal Waiter(ResourcePipeline pipeline, ResourceKey key, CancellationToken token,
            IProgress<ResourceProgress>? progress) : base(key, token, progress)
        {
            _pipeline = pipeline;
        }

        internal ResourceRef<T>? DeliveredReference { get; private set; }

        internal override bool HasDeliveredReference => DeliveredReference != null;

        /// <summary> 命中复用：现场建立持有，不排队也不建记录 </summary>
        internal static Waiter<T> Granted(ResourcePipeline pipeline, ResourceEntry entry,
            CancellationToken token, IProgress<ResourceProgress>? progress)
        {
            var waiter = new Waiter<T>(pipeline, entry.Key, token, progress);
            waiter.DeliveredReference = entry.CreateReference<T>();
            waiter.Result = WaitResult.Granted;
            waiter.MarkReleased();
            return waiter;
        }

        internal override bool TryGrant(ResourceEntry entry)
        {
            if (Token.IsCancellationRequested)
            {
                Cancel(false);
                return false;
            }
            try
            {
                // 引用分配与终态置位连在一起，取消回调只可能观察到两者之一。
                DeliveredReference = entry.CreateReference<T>();
                Result = WaitResult.Granted;
                return true;
            }
            catch (ResourceLoadException error)
            {
                // 请求类型与本轮产物不符：只让该项失败，其余兼容请求照常取得引用。
                Fail(error.Error);
                return false;
            }
        }
    }

    /// <summary>
    /// 已登记产物：保存具体产物、持有计数与复用索引关系；不调度加载或卸载。
    /// 失效产物只撤下复用索引，不触发释放；旧引用仍绑定原对象，同键新旧产物可以共存。
    /// </summary>
    internal sealed class ResourceEntry
    {
        private readonly ResourcePipeline _pipeline;
        private readonly TaskCompletionSource<bool> _removal =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invalid;

        internal ResourceEntry(ResourcePipeline pipeline, ResourceKey key, LoadedAsset asset, long leaseId)
        {
            _pipeline = pipeline;
            Key = key;
            Asset = asset;
            LeaseId = leaseId;
        }

        internal ResourceKey Key { get; }

        internal LoadedAsset Asset { get; }

        internal long LeaseId { get; }

        internal int HoldCount { get; private set; }

        internal bool IsUnloading { get; private set; }

        internal Task RemovalTask => _removal.Task;

        /// <summary> 产物是否仍存活；查询异常不等于确认已销毁，只按失效处理 </summary>
        internal bool IsAlive
        {
            get
            {
                if (Volatile.Read(ref _invalid) != 0) return false;
                if (!ResourcePipeline.IsAssetAlive(Asset))
                {
                    Volatile.Write(ref _invalid, 1);
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// 取得释放责任：已经在释放中的返回 false，调用方沿用其完成通知。
        /// force 用于退出：退出不等待业务归还引用，所以不要求持有归零。
        /// </summary>
        internal bool TryBeginUnload(bool force = false)
        {
            if (IsUnloading) return false;
            if (!force && HoldCount != 0) return false;
            IsUnloading = true;
            return true;
        }

        internal void CompleteRemoval(LoadError? failure) => _removal.TrySetResult(true);

        /// <summary> 为一次业务调用建立独立持有；类型不符时抛出类型错误，只影响该次调用 </summary>
        internal ResourceRef<T> CreateReference<T>() where T : class
        {
            if (!(Asset.Value is T value))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetTypeMismatch, LoadStage.LoadAsset, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "key", Key.ToString() },
                        { "requestedType", typeof(T).Name },
                        { "actualType", Asset.Value.GetType().Name },
                    }));
            }
            HoldCount++;
            return new ResourceRef<T>(_pipeline.Context, this, value, LeaseId);
        }

        /// <summary> 归还一次持有；重复归还返回 false 且不改变计数 </summary>
        internal bool ReleaseHold()
        {
            if (HoldCount <= 0)
            {
                _pipeline.Diagnostics.Report(new LoadError(
                    DiagnosticCodes.InternalConsistency, LoadStage.ReleaseAsset, CleanupStatus.Unknown, null,
                    new Dictionary<string, object> { { "key", Key.ToString() }, { "leaseId", LeaseId } }));
                return false;
            }
            HoldCount--;
            return true;
        }

        /// <summary> 追加一份持有；产物已无人持有时返回 false </summary>
        internal bool TryRetain()
        {
            if (HoldCount <= 0) return false;
            HoldCount++;
            return true;
        }

        internal Dictionary<string, object> DiagnosticContext() => new Dictionary<string, object>
        {
            { "source", Key.Source },
            { "key", Key.ToString() },
        };
    }

    /// <summary>
    /// 一次加载：同键的业务请求合并到这里，记录本身推进"是否还接受新接收者"与"是否已经批准启动"，
    /// 但执行由 ResourcePipeline 推进。记录活到整个收尾完成，摘表与终结同一次提交。
    /// </summary>
    internal sealed class ResourceRecord : IProgress<ResourceProgress>
    {
        private readonly ResourcePipeline _pipeline;
        private readonly List<Waiter> _waiters = new List<Waiter>();
        private readonly TaskCompletionSource<bool> _finished =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private long _waiterSeed;

        internal ResourceRecord(ResourcePipeline pipeline, ResourceKey key, ResolvedResource resolved,
            LoaderRegistration registration, string operationId)
        {
            _pipeline = pipeline;
            Key = key;
            Resolved = resolved;
            Registration = registration;
            OperationId = operationId;
        }

        internal ResourceKey Key { get; }

        internal ResolvedResource Resolved { get; }

        internal LoaderRegistration Registration { get; }

        internal string OperationId { get; }

        internal bool AcceptingNewReceivers { get; private set; } = true;

        internal bool Abandoned { get; private set; }

        /// <summary> 是否已经取到额度并批准启动；只表示后端是否开跑，不代表执行责任归属 </summary>
        internal bool Started { get; private set; }

        /// <summary> 执行责任只被认领一次：登记路径与放弃路径通过它竞争同一个执行循环 </summary>
        internal bool TryClaimExecution() => Interlocked.CompareExchange(ref _executionClaimed, 1, 0) == 0;

        private int _executionClaimed;

        internal ResourceEntry? Entry { get; private set; }

        internal CancellationToken OperationToken => _cancellation.Token;

        internal Task Finished => _finished.Task;

        internal int WaitingCount => _waiters.Count;

        /// <summary>
        /// 加入本轮：只有仍在接受接收者、且尚未被放弃的记录才允许加入。
        /// 已经放弃或终结的记录返回 null，调用方改为等待它结束收尾后重新解析。
        /// </summary>
        internal Waiter<T>? Attach<T>(CancellationToken token, IProgress<ResourceProgress>? progress) where T : class
        {
            if (!AcceptingNewReceivers || Abandoned) return null;
            var waiter = new Waiter<T>(_pipeline, Key, token, progress) { Sequence = ++_waiterSeed };
            _waiters.Add(waiter);
            return waiter;
        }

        /// <summary> 等待者退出等待 </summary>
        internal bool Remove(Waiter waiter) => _waiters.Remove(waiter);

        /// <summary> 标记放弃：关闭准入，并让已经开始的执行在收尾分支处理迟到产物 </summary>
        internal void MarkAbandoned()
        {
            Abandoned = true;
            CloseAdmission();
        }

        internal void CloseAdmission() => AcceptingNewReceivers = false;

        /// <summary>
        /// 批准启动：把当前等待者定为本轮参与者，并选出本轮的归属者。
        /// 参与者在批准时固定下来，中途加入的请求属于下一轮，不被这一轮的结果波及。
        /// </summary>
        internal void ApproveStart()
        {
            Started = true;
            if (Participants != null) return;
            var participants = new List<Waiter>(_waiters);
            Participants = participants;
            // 归属者取最早发起、且尚未承担过失败的请求：与"谁先来谁承担"一致，且可预期。
            Waiter? candidate = null;
            foreach (var waiter in participants)
            {
                if (waiter.HasBorneFailure) continue;
                if (candidate == null || waiter.Sequence < candidate.Sequence) candidate = waiter;
            }
            AttemptLeader = candidate;
        }

        /// <summary> 本轮的参与者与归属者；只在批准启动时确定一次 </summary>
        internal List<Waiter>? Participants { get; private set; }

        internal Waiter? AttemptLeader { get; private set; }

        internal void Bind(ResourceEntry entry) => Entry = entry;

        internal void CancelBackend()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { } // 后端可能已经完成并释放取消源。
            catch (Exception error)
            {
                _pipeline.Diagnostics.Report(new LoadError(DiagnosticCodes.ObserverCallbackFailed,
                    LoadStage.WaitForLoad, CleanupStatus.Unknown, error, DiagnosticContext()));
            }
        }

        /// <summary> 交付提交取走本轮的接收者名册 </summary>
        internal List<Waiter> CollectDeliveries()
        {
            var collected = new List<Waiter>(_waiters.Count + 1);
            foreach (var waiter in TakeRoster())
            {
                if (waiter.Token.IsCancellationRequested)
                {
                    waiter.Cancel(false);
                    continue;
                }
                collected.Add(waiter);
            }
            return collected;
        }

        /// <summary>
        /// 取走本轮要通知的全部接收者：批准启动时的名册 + 批准之后、交付之前加入的接收者。
        /// 名册在这里一次性定下，之后的加入者属于下一轮。
        /// </summary>
        private List<Waiter> TakeRoster()
        {
            var roster = new List<Waiter>(_waiters.Count + 1);
            var participants = Participants;
            Participants = null;
            if (participants != null) roster.AddRange(participants);
            foreach (var waiter in _waiters)
            {
                if (!roster.Contains(waiter)) roster.Add(waiter);
            }
            _waiters.Clear();
            return roster;
        }
        /// <summary>
        /// 终结：只种一个终态，并一次性通知所有与本轮相关的请求。
        /// 本轮参与者按<b>来源固定的传播规则</b>结算：
        /// 本地类来源本批共同结束；远端来源只有承担者失败、其余接续。
        /// 个人取消、超时、生命周期退出与共享加载失败是不同事件，不套用这张表。
        /// </summary>
        internal void Complete(LoadError? failure)
        {
            CloseAdmission(); // 终结之后不再接受新的接收者
            _finished.TrySetResult(true);
            var participants = TakeRoster();
            var leader = AttemptLeader;
            AttemptLeader = null;

            // 来源在记录创建时确定，不随后续替换加载器改变。
            var spread = SpreadOf(Key.Source);

            foreach (var waiter in participants)
            {
                try
                {
                    var noLeaderLeft = leader == null;
                    if (failure == null || noLeaderLeft || waiter.WasCanceled || waiter.Token.IsCancellationRequested)
                    {
                        waiter.Cancel(waiter.TimedOut);
                        continue;
                    }

                    var disposed = failure.DiagnosticCode == DiagnosticCodes.LifecycleManagerDisposed;
                    var isLeader = ReferenceEquals(waiter, leader);
                    // 生命周期退出与"本批共同结束"都结算所有仍有效的接收者；后者交同一个真实原因。
                    if (disposed || isLeader || spread == LoadFailureSpread.Batch)
                    {
                        // 承担过失败的请求不再被推为下一轮承担者：每轮消耗一名承担者，没有隐式无限重试。
                        if (isLeader) waiter.MarkBorneFailure();
                        waiter.Fail(failure);
                    }
                    else
                    {
                        waiter.Retry();
                    }
                }
                catch (Exception)
                {
                    // 单个等待者结算失败不得让其余等待者失去通知：终止态的种入本身就是 try/catch 保护的。
                    waiter.Cancel(false);
                }
            }
        }

        /// <summary>
        /// 来源 → 失败传播规则。固定映射，只在资源编排这一处表达：
        /// 替换某来源的加载器不改变该来源的传播行为，加载器也只交真实原因、不判断"永远失败还是暂时失败"。
        /// </summary>
        internal static LoadFailureSpread SpreadOf(ResourceSource source)
        {
            return source == ResourceSource.Remote ? LoadFailureSpread.LeaderOnly : LoadFailureSpread.Batch;
        }

        internal void DisposeCancellation()
        {
            try { _cancellation.Dispose(); }
            catch (Exception) { }
        }

        internal Dictionary<string, object> DiagnosticContext() => new Dictionary<string, object>
        {
            { "source", Key.Source },
            { "key", Key.ToString() },
            { "operationId", OperationId },
        };

        /// <summary> 本次加载向自己的接收者报告进度；失败的回调只报诊断，不影响流程 </summary>
        public void Report(ResourceProgress value)
        {
            var targets = new Waiter[_waiters.Count];
            _waiters.CopyTo(targets);
            _pipeline.Context.Post(() =>
            {
                foreach (var waiter in targets)
                {
                    if (waiter.Released) continue;
                    var progress = waiter.Progress;
                    if (progress == null) continue;
                    try { progress.Report(value); }
                    catch (Exception error)
                    {
                        _pipeline.Diagnostics.Report(new LoadError(
                            DiagnosticCodes.ObserverCallbackFailed, value.Stage, CleanupStatus.Complete, error));
                    }
                }
            });
        }
    }
}