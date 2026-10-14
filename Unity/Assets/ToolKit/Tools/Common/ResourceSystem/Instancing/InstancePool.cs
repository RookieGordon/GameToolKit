/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 实例池 (P3, §7)。PoolKey (原型 ResourceKey + FactoryId + InstanceKey) 定位桶；
 *                每桶持有一份原型引用，Idle/Active/Creating/Preparing/Returning/Destroying 计数
 *                任何一种非零都不能释放原型。首版每桶同时最多一次创建，等待者 FIFO。
 *                所有工厂回调都可能触发用户脚本，回调返回后重查 manager、桶状态和等待者状态。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 待交付请求：独立取消，以完成任务记录唯一终态。 </summary>
    internal sealed class RentWaiter
    {
        public readonly Type RequestedType;
        public readonly TaskCompletionSource<object> Completion;
        public readonly CancellationToken CallerToken;
        public readonly Func<PoolBucket, InstanceRecord, object> LeaseFactory;

        public RentWaiter(
            Type requestedType,
            CancellationToken callerToken,
            Func<PoolBucket, InstanceRecord, object> leaseFactory)
        {
            RequestedType = requestedType;
            CallerToken = callerToken;
            LeaseFactory = leaseFactory;
            Completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary> 实例及归还时间；所属的 Active/Idle 集合表达当前归属。 </summary>
    internal sealed class InstanceRecord
    {
        public readonly long LeaseId;
        public readonly object Value;
        public double ReturnedAt;

        public InstanceRecord(long leaseId, object value)
        {
            LeaseId = leaseId;
            Value = value;
        }
    }

    /// <summary> 池桶：同 PoolKey 的一个代际；Generation 只区分生命周期，不是业务 token </summary>
    internal sealed class PoolBucket
    {
        public readonly PoolKey Key;
        public readonly long Generation;
        public readonly FactoryRegistration FactoryReg;
        public readonly PoolPolicy Policy;
        public readonly ResourceRequest CreationRequest;
        /// <summary> 全部有效业务请求关联：工厂可将原型映射到其他资源来源 (R33) </summary>
        public readonly HashSet<(ResourceSource Source, string Address, string Factory)> Associations =
            new HashSet<(ResourceSource Source, string Address, string Factory)>();
        public PoolState State;
        public ResourceRef<object>? Prototype;
        public readonly Queue<InstanceRecord> Idle = new Queue<InstanceRecord>();
        public readonly Dictionary<long, InstanceRecord> Active = new Dictionary<long, InstanceRecord>();
        public readonly List<RentWaiter> Waiters = new List<RentWaiter>();
        public int Creating;
        public int Preparing;
        public int Returning;
        public int Destroying;
        public bool InitializationPending = true;
        public bool Pumping; // 同步重入门闩：嵌套 Pump 由外层循环继续处理
        public CancellationTokenSource OperationCts = new CancellationTokenSource();
        public readonly TaskCompletionSource<object> Closed =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        public double LastActivity;
        public LoadError? CloseError;

        public PoolBucket(
            PoolKey key,
            long generation,
            FactoryRegistration factoryReg,
            PoolPolicy policy,
            ResourceRequest creationRequest)
        {
            Key = key;
            Generation = generation;
            FactoryReg = factoryReg;
            Policy = policy;
            CreationRequest = creationRequest;
        }

        public bool HasInFlight =>
            Creating != 0 || Preparing != 0 || Returning != 0 || Destroying != 0;

        public bool IsEmptyOfInstances =>
            Idle.Count == 0 && Active.Count == 0;
    }

    internal sealed class InstancePool
    {
        private readonly IExecutionContext _context;
        private readonly LoadManager _loads;
        private readonly IResourceDiagnostics _diagnostics;
        private readonly Func<double> _monotonicNow;
        private readonly PoolPolicy _defaultPolicy;
        private readonly Func<bool> _isDisposed;

        private readonly Dictionary<PoolKey, PoolBucket> _currentBuckets = new Dictionary<PoolKey, PoolBucket>();
        private readonly List<PoolBucket> _trackedBuckets = new List<PoolBucket>(); // 含退役桶，直到实际实例操作结束
        private long _generationSeed;
        private long _recordSeed;

        internal InstancePool(
            IExecutionContext context,
            LoadManager loads,
            IResourceDiagnostics diagnostics,
            Func<double> monotonicNow,
            PoolPolicy defaultPolicy,
            Func<bool> isDisposed)
        {
            _context = context;
            _loads = loads;
            _diagnostics = diagnostics;
            _monotonicNow = monotonicNow;
            _defaultPolicy = defaultPolicy;
            _isDisposed = isDisposed;
        }

        internal IExecutionContext Context => _context;
        internal bool IsDisposed => _isDisposed();

        #region 租用入口

        /// <summary>
        /// 在上下文内登记租用。新桶交给调用方在状态提交之后初始化；已有桶直接尝试交付。
        /// 返回各调用者的独立任务，只有创建桶的调用者取得初始化责任。
        /// </summary>
        internal (Task<object> Completion, PoolBucket? BucketToInitialize) JoinOrRent(
            PoolKey poolKey,
            FactoryRegistration factoryReg,
            ResourceRequest instanceRequest,
            Type requestedType,
            Func<PoolBucket, InstanceRecord, object> leaseFactory,
            CancellationToken callerCt)
        {
            if (_isDisposed())
            {
                throw new ResourceLoadException(_ManagerDisposed());
            }
            callerCt.ThrowIfCancellationRequested();

            if (_currentBuckets.TryGetValue(poolKey, out var bucket))
            {
                // 同一池可由多个业务地址到达：登记本请求的关联 (R33)
                bucket.Associations.Add((instanceRequest.Source, instanceRequest.Address, factoryReg.Name));
                var task = _AddWaiter(bucket, requestedType, leaseFactory, callerCt);
                _Pump(bucket); // 已开放桶的新等待者立即尝试交付 (闲置实例或触发创建)
                return (task, null);
            }

            bucket = new PoolBucket(
                poolKey,
                Interlocked.Increment(ref _generationSeed),
                factoryReg,
                factoryReg.Policy ?? _defaultPolicy,
                instanceRequest)
            {
                State = PoolState.Initializing,
                LastActivity = _monotonicNow(),
            };
            bucket.Associations.Add((instanceRequest.Source, instanceRequest.Address, factoryReg.Name));
            _currentBuckets[poolKey] = bucket;
            _trackedBuckets.Add(bucket);
            var waiterTask = _AddWaiter(bucket, requestedType, leaseFactory, callerCt);
            return (waiterTask, bucket);
        }

        private async Task<object> _AddWaiter(
            PoolBucket bucket,
            Type requestedType,
            Func<PoolBucket, InstanceRecord, object> leaseFactory,
            CancellationToken callerCt)
        {
            var w = new RentWaiter(requestedType, callerCt, leaseFactory);
            bucket.Waiters.Add(w);
            using (callerCt.Register(() => _context.Post(() => _CancelWaiter(bucket, w))))
                return await w.Completion.Task.ConfigureAwait(false);
        }

        private void _CancelWaiter(PoolBucket bucket, RentWaiter w)
        {
            if (w.Completion.Task.IsCompleted)
            {
                return;
            }
            bucket.Waiters.Remove(w);
            w.Completion.TrySetCanceled();
        }

        private void _FailWaiter(PoolBucket bucket, RentWaiter w, LoadError error)
        {
            if (w.Completion.Task.IsCompleted)
            {
                return;
            }
            bucket.Waiters.Remove(w);
            w.Completion.TrySetException(new ResourceLoadException(error));
        }

        /// <summary> 结算本桶待交付请求；个人取消优先，error 为 null 表示桶操作已取消。 </summary>
        private void _FailPendingRents(PoolBucket bucket, LoadError? error)
        {
            foreach (var waiter in bucket.Waiters.ToArray())
            {
                if (error == null || waiter.CallerToken.IsCancellationRequested)
                    _CancelWaiter(bucket, waiter);
                else
                    _FailWaiter(bucket, waiter, error);
            }
        }

        /// <summary>
        /// 调用方离开登记租用的状态提交后执行。即使首个请求取消，仍完成初始化或取消收尾，
        /// 因为桶可能已有其他等待者；原型始终使用建桶时确定的解析结果。
        /// </summary>
        internal async Task InitializeAsync(
            PoolBucket bucket,
            LoaderRegistration protoRegistration,
            ResolvedResource protoResolved)
        {
            ResourceRef<object>? prototype = null;
            Exception? error = null;
            try
            {
                prototype = await _loads.AcquireResolvedAsync(
                    protoRegistration, protoResolved, bucket.OperationCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            _context.Post(() => _OnPrototypeAcquired(bucket, prototype, error));
        }

        private void _OnPrototypeAcquired(PoolBucket bucket, ResourceRef<object>? prototype, Exception? error)
        {
            try
            {
                _CommitPrototype(bucket, prototype, error);
            }
            finally
            {
                bucket.InitializationPending = false;
                _CheckBucketClosed(bucket);
            }
        }

        private void _CommitPrototype(PoolBucket bucket, ResourceRef<object>? prototype, Exception? error)
        {
            if (bucket.State == PoolState.Closing || bucket.State == PoolState.Closed)
            {
                prototype?.Dispose();
                return;
            }

            if (error != null)
            {
                // 原型加载失败：结算本桶等待者；具体残留由加载流程独立记录。
                var loadError = error is OperationCanceledException && bucket.OperationCts.Token.IsCancellationRequested
                    ? null // 正常取消：等待者按各自令牌取消，不发故障日志
                    : error is ResourceLoadException rle
                        ? rle.Error
                        : new LoadError(DiagnosticCodes.InternalUnexpected, LoadStage.LoadAsset,
                            CleanupStatus.Unknown, error, _BucketContext(bucket, "prototype"));
                _FailPendingRents(bucket, loadError);
                _FinishInitialization(bucket, prototype, report: loadError);
                return;
            }

            // 原型引用已取得：此后任何失败都必须归还原型，所有权只在写入 bucket.Prototype 后转交 (R09/R23)
            object? protoValue = null;
            LoadError? failure = null;
            try
            {
                protoValue = prototype!.Value;
            }
            catch (Exception ex)
            {
                // 底层对象被外部销毁：可解释的初始化失败，回退原型
                failure = new LoadError(
                    DiagnosticCodes.AssetInvalidated, LoadStage.Instantiate,
                    CleanupStatus.Complete, ex, _BucketContext(bucket, "prototype-invalidated"));
            }

            if (failure == null)
            {
                try
                {
                    if (!bucket.FactoryReg.Factory.CanCreate(protoValue!, bucket.CreationRequest.RequestedType))
                    {
                        failure = new LoadError(
                            DiagnosticCodes.InstanceUnsupported, LoadStage.Instantiate,
                            CleanupStatus.Complete, null,
                            _BucketContext(bucket, "CanCreate")
                                .Also(c => c.Add("prototypeType", protoValue!.GetType().Name)));
                    }
                }
                catch (Exception ex)
                {
                    // 查询回调异常按当前阶段归类并走清理路径，不能悬挂等待者 (R09)
                    failure = new LoadError(
                        DiagnosticCodes.InstanceUnsupported, LoadStage.Instantiate,
                        CleanupStatus.Complete, ex, _BucketContext(bucket, "CanCreate:threw"));
                }
            }

            if (failure != null)
            {
                _FailPendingRents(bucket, failure);
                _FinishInitialization(bucket, prototype, report: failure);
                return;
            }

            // CanCreate 可以重入退出或清池，不能在回调后重新开放已经关闭的桶。
            if (bucket.State != PoolState.Initializing || _isDisposed())
            {
                prototype?.Dispose();
                return;
            }
            bucket.Prototype = prototype; // 所有权转交桶
            bucket.State = PoolState.Open;
            _Pump(bucket);
        }

        /// <summary> 初始化失败的统一收尾：结算等待者后归还原型、移除桶、终结 Closed (不递归、不丢引用) </summary>
        private void _FinishInitialization(PoolBucket bucket, ResourceRef<object>? prototype, LoadError? report)
        {
            if (report != null)
            {
                _diagnostics.Report(report);
            }
            prototype?.Dispose();
            _RemoveBucket(bucket);
            bucket.OperationCts.Dispose();
            bucket.Closed.TrySetResult(null!);
        }

        #endregion

        #region 交付泵 (§7.3)

        private void _Pump(PoolBucket bucket)
        {
            if (bucket.State != PoolState.Open || bucket.Pumping)
            {
                return;
            }
            bucket.Pumping = true;
            try
            {
                _PumpCore(bucket);
            }
            finally
            {
                bucket.Pumping = false;
                _CheckBucketClosed(bucket);
            }
        }

        private void _PumpCore(PoolBucket bucket)
        {
            while (true)
            {
                var waiter = _FirstPendingWaiter(bucket);
                if (waiter == null)
                {
                    break;
                }
                if (!_QuotaAvailable(bucket))
                {
                    break; // 配额占用中：归还或销毁确认后再次 Pump
                }

                if (bucket.Idle.Count > 0)
                {
                    var record = bucket.Idle.Dequeue();
                    var alive = _TryFactoryIsAlive(bucket, record.Value);
                    if (alive == false)
                    {
                        continue;
                    }
                    if (alive == null)
                    {
                        // 存活查询失败：保守销毁，不得当作已销毁交付 (R26)
                        _ = _RunDestroyAsync(bucket, record);
                        continue;
                    }

                    bucket.Preparing++;
                    try
                    {
                        bucket.FactoryReg.Factory.OnRent(record.Value);
                    }
                    catch (Exception ex)
                    {
                        // 先结算等待者，再销毁实例：同步销毁回调重入 Pump 时不再看到待交付请求
                        bucket.Preparing--;
                        _FailWaiter(bucket, waiter, _ResetFailed(bucket, "OnRent", ex));
                        _ = _RunDestroyAsync(bucket, record);
                        continue;
                    }

                    if (!waiter.RequestedType.IsInstanceOfType(record.Value))
                    {
                        bucket.Preparing--;
                        _FailWaiter(bucket, waiter, new LoadError(
                            DiagnosticCodes.InstanceUnsupported, LoadStage.Instantiate,
                            CleanupStatus.Complete, null,
                            _BucketContext(bucket, "instance-type")
                                .Also(c =>
                                {
                                    c.Add("requestedType", waiter.RequestedType.Name);
                                    c.Add("actualType", record.Value.GetType().Name);
                                })));
                        _ = _RunDestroyAsync(bucket, record);
                        continue;
                    }

                    // 最终交付前再次检查 manager、桶和等待者
                    if (_isDisposed() || bucket.State != PoolState.Open
                        || waiter.Completion.Task.IsCompleted || waiter.CallerToken.IsCancellationRequested)
                    {
                        bucket.Preparing--;
                        if (!waiter.Completion.Task.IsCompleted && waiter.CallerToken.IsCancellationRequested)
                        {
                            _CancelWaiter(bucket, waiter);
                        }
                        _ = _RunDestroyAsync(bucket, record);
                        continue;
                    }

                    bucket.Preparing--;
                    bucket.Active[record.LeaseId] = record;
                    bucket.Waiters.Remove(waiter);
                    var lease = waiter.LeaseFactory(bucket, record);
                    bucket.LastActivity = _monotonicNow();
                    waiter.Completion.TrySetResult(lease); // 先记录租约，再完成等待者
                }
                else if (bucket.Creating == 0)
                {
                    _ = _CreateInstanceAsync(bucket);
                    // 同步完成的创建会在重入中完成记账；异步创建由完成回调 Pump。
                    // continue 而不是 break：两种情况都由本循环重新评估。
                    continue;
                }
                else
                {
                    break;
                }
            }
        }

        private RentWaiter? _FirstPendingWaiter(PoolBucket bucket)
        {
            while (bucket.Waiters.Count > 0)
            {
                var head = bucket.Waiters[0];
                if (!head.CallerToken.IsCancellationRequested)
                    return head;
                _CancelWaiter(bucket, head);
            }
            return null;
        }

        private bool _QuotaAvailable(PoolBucket bucket)
        {
            var max = bucket.Policy.MaxActivePerResource;
            return max == null || bucket.Active.Count + bucket.Preparing + bucket.Creating < max.Value;
        }

        private async Task _CreateInstanceAsync(PoolBucket bucket)
        {
            bucket.Creating = 1;
            bucket.LastActivity = _monotonicNow();
            object? instance = null;
            Exception? error = null;
            try
            {
                var prototypeValue = bucket.Prototype!.Value; // 上下文内访问
                instance = await bucket.FactoryReg.Factory.CreateAsync(
                    prototypeValue, bucket.CreationRequest, bucket.OperationCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bucket.OperationCts.Token.IsCancellationRequested)
            {
                // 桶关闭取消：无交付责任
            }
            catch (Exception ex)
            {
                error = ex;
            }

            _context.Post(() => _OnCreateCompleted(bucket, instance, error));
        }

        private void _OnCreateCompleted(PoolBucket bucket, object? instance, Exception? error)
        {
            try
            {
                _CommitCreatedInstance(bucket, instance, error);
            }
            finally
            {
                // 回调处理和迟到实例的销毁安排结束，才结束这次创建的在途保护。
                bucket.Creating = 0;
                _Pump(bucket);
                _CheckBucketClosed(bucket);
            }
        }

        private void _CommitCreatedInstance(PoolBucket bucket, object? instance, Exception? error)
        {
            // 失败结果与清理语义优先处理：关闭不改变失败回退的责任 (R07)
            if (error != null)
            {
                var err = _ClassifyFactoryFailure(error, bucket, "CreateAsync");
                if (error is ResourceLoadException failed && failed.CleanupRemainders.Count > 0)
                    _RecordDirtyInstances(bucket, err, failed.CleanupRemainders);
                // 清理工厂未交付对象的责任在工厂；失败队首有效等待者后继续，避免无限重试
                var waiter = _FirstPendingWaiter(bucket);
                if (waiter != null)
                {
                    _FailWaiter(bucket, waiter, err);
                }
                _diagnostics.Report(err);
                return;
            }

            var alive = instance == null ? false : _TryFactoryIsAlive(bucket, instance);
            if (alive != true)
            {
                // 先结算等待者，再安排销毁；查询异常不能当作对象已销毁。
                var waiter = _FirstPendingWaiter(bucket);
                if (waiter != null)
                {
                    _FailWaiter(bucket, waiter, new LoadError(
                        DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate,
                        CleanupStatus.Complete, null,
                        _BucketContext(bucket, alive == null ? "CreateAsync:alive-query-failed" : "CreateAsync:null-result")));
                }
                if (alive == null)
                    _DestroyNewInstance(bucket, instance!);
                return;
            }

            if (bucket.State != PoolState.Open || _isDisposed())
            {
                _DestroyNewInstance(bucket, instance!);
                return;
            }

            var record = new InstanceRecord(Interlocked.Increment(ref _recordSeed), instance!);
            if (_FirstPendingWaiter(bucket) == null)
            {
                // 无人等待：与普通归还同一状态流 (Returning 保护 + 回调后重查)，
                // 不得使用缺少记账保护的旁路 (R08)
                _ReturnRecord(bucket, record);
                return;
            }
            // 作为未交付候选进入闲置队列，由 Pump 执行 OnRent 并交付
            bucket.Idle.Enqueue(record);
        }

        private void _DestroyNewInstance(PoolBucket bucket, object instance)
        {
            var record = new InstanceRecord(Interlocked.Increment(ref _recordSeed), instance);
            _ = _RunDestroyAsync(bucket, record);
        }

        #endregion

        #region 归还、销毁与桶状态 (§7.3-§7.4)

        /// <summary> 归还租约记录的原池；必须通过 lease 一次性门闩进入 </summary>
        internal void Return(PoolBucket bucket, InstanceRecord record)
        {
            // 退出已接管所有实例；迟到归还不再重复销毁或报告未知记录。
            if (_isDisposed()) return;
            if (!bucket.Active.Remove(record.LeaseId))
            {
                _diagnostics.Report(new LoadError(
                    DiagnosticCodes.InternalConsistency, LoadStage.ReturnInstance,
                    CleanupStatus.Unknown, null,
                    _BucketContext(bucket, "Return:unknown-record").Also(c => c.Add("record", record.LeaseId))));
                return;
            }
            _ReturnRecord(bucket, record);
        }

        /// <summary>
        /// 归还一条实例记录的统一状态流 (R08)：存活检查 → Returning 保护下回调 → 回调后重查 →
        /// 入闲置或销毁；查询失败保守销毁，不当作已销毁 (R26)。
        /// </summary>
        private void _ReturnRecord(PoolBucket bucket, InstanceRecord record)
        {
            bucket.Returning++;
            try
            {
                bucket.LastActivity = _monotonicNow();
                var alive = _TryFactoryIsAlive(bucket, record.Value);
                if (alive == false) return;

                LoadError? resetFailure = null;
                if (alive == true && bucket.State == PoolState.Open && !_isDisposed()
                    && bucket.Idle.Count < bucket.Policy.MaxIdlePerResource)
                {
                    try
                    {
                        bucket.FactoryReg.Factory.OnReturn(record.Value);
                    }
                    catch (Exception ex)
                    {
                        resetFailure = _ResetFailed(bucket, "OnReturn", ex);
                    }
                    // 回调可能重入 ClearPool/Dispose，只有成功重置且仍可缓存才交回 Idle。
                    if (resetFailure == null && bucket.State == PoolState.Open && !_isDisposed()
                        && bucket.Idle.Count < bucket.Policy.MaxIdlePerResource)
                    {
                        record.ReturnedAt = _monotonicNow();
                        bucket.Idle.Enqueue(record);
                        return;
                    }
                }

                // 查询异常、重置失败、池已关闭或容量不足，统一交给销毁流程。
                _ = _RunDestroyAsync(bucket, record);
                if (resetFailure != null) _diagnostics.Report(resetFailure);
            }
            finally
            {
                bucket.Returning--;
                _Pump(bucket);
                _CheckBucketClosed(bucket);
            }
        }

        private async Task _RunDestroyAsync(PoolBucket bucket, InstanceRecord record)
        {
            // 在首次 await 前登记；同步回调和真正异步销毁都受同一在途保护。
            bucket.Destroying++;
            Exception? error = null;
            try
            {
                await bucket.FactoryReg.Factory.DestroyAsync(record.Value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            await _context.RunAsync(() =>
            {
                bucket.Destroying--;
                if (error != null)
                {
                    var err = new LoadError(
                        DiagnosticCodes.InstanceDestroyFailed, LoadStage.DestroyInstance,
                        CleanupStatus.Incomplete, error,
                        _BucketContext(bucket, "DestroyAsync").Also(c => c.Add("record", record.LeaseId)));
                    var remnants = error is ResourceLoadException failed && failed.CleanupRemainders.Count > 0
                        ? failed.CleanupRemainders : new object[] { record.Value };
                    _RecordDirtyInstances(bucket, err, remnants);
                    _diagnostics.Report(err);
                }
                // 只隔离失败的实例，其他实例和后继请求继续使用本池。
                _Pump(bucket);
                _CheckBucketClosed(bucket);
            }).ConfigureAwait(false);
        }

        private void _CloseBucket(PoolBucket bucket)
        {
            if (bucket.State == PoolState.Closing || bucket.State == PoolState.Closed)
            {
                return;
            }
            bucket.State = PoolState.Closing;
            if (_currentBuckets.TryGetValue(bucket.Key, out var current) && ReferenceEquals(current, bucket))
            {
                _currentBuckets.Remove(bucket.Key);
            }

            var closed = new LoadError(
                DiagnosticCodes.InstancePoolClosed, LoadStage.Instantiate, CleanupStatus.Complete,
                null, _BucketContext(bucket, "CloseBucket"));
            _FailPendingRents(bucket, closed);

            // 取消尚未开始的创建；已开始的创建仍负责观察并清理结果
            _CancelBucketOperations(bucket);
            while (bucket.Idle.Count > 0)
            {
                _ = _RunDestroyAsync(bucket, bucket.Idle.Dequeue());
            }
            _CheckBucketClosed(bucket);
        }

        private void _CheckBucketClosed(PoolBucket bucket)
        {
            if (bucket.InitializationPending || !bucket.IsEmptyOfInstances || bucket.HasInFlight || bucket.Pumping)
            {
                return;
            }
            if (bucket.State == PoolState.Closing)
            {
                bucket.Prototype?.Dispose(); // 实际实例操作结束，才能归还原型持有。
                bucket.Prototype = null;
                bucket.State = PoolState.Closed;
                _RemoveBucket(bucket);
                bucket.OperationCts.Dispose();
                if (bucket.CloseError == null) bucket.Closed.TrySetResult(null!);
                else
                {
                    bucket.Closed.TrySetException(new ResourceLoadException(bucket.CloseError));
                    _ = bucket.Closed.Task.Exception; // 定期回收未必有调用者等待。
                }
            }
        }

        /// <summary> 残留实例独立隔离，并保留其原型持有；不使整个池或后来的同键池失效。 </summary>
        private void _RecordDirtyInstances(PoolBucket bucket, LoadError error, IReadOnlyList<object> remnants)
        {
            var retained = new List<object>(remnants);
            var prototype = bucket.Prototype;
            if (!_isDisposed() && prototype != null)
                retained.Add(prototype.RetainForCleanup());
            _loads.RecordCleanupFailure(bucket.Key.Prototype, error, retained);
            if (bucket.State == PoolState.Closing)
                bucket.CloseError = bucket.CloseError == null ? error : bucket.CloseError.WithRelated(error);
        }

        private void _RemoveBucket(PoolBucket bucket)
        {
            if (_currentBuckets.TryGetValue(bucket.Key, out var current) && ReferenceEquals(current, bucket))
            {
                _currentBuckets.Remove(bucket.Key);
            }
            _trackedBuckets.Remove(bucket);
        }

        #endregion

        #region 维护、关闭与查询

        /// <summary>
        /// 关闭在调用时捕获的、与业务请求 (source, address, factory) 关联的全部池代际 (R33)：
        /// 关联保存原始业务请求，工厂将原型映射到其他资源来源时同样可以按原参数清池。
        /// </summary>
        internal List<Task> CloseBuckets(string address, ResourceSource source, string factory)
        {
            var association = (source, address, factory);
            var tasks = new List<Task>();
            foreach (var bucket in _trackedBuckets.ToArray())
            {
                if (bucket.Associations.Contains(association))
                {
                    tasks.Add(bucket.Closed.Task);
                    _CloseBucket(bucket);
                }
            }
            return tasks;
        }

        /// <summary> 进程退出：接管全部实例并销毁，内部只等待实际操作收尾，不等待租约归还。 </summary>
        internal async Task DisposeAsync()
        {
            var buckets = _trackedBuckets.ToArray();
            var completions = new List<Task>(buckets.Length);
            foreach (var bucket in buckets)
            {
                completions.Add(bucket.Closed.Task);
                _CloseBucket(bucket);
                // Preparing/Returning 的对象由正在执行的回调收尾；这里只接管已经交付的实例。
                foreach (var record in new List<InstanceRecord>(bucket.Active.Values))
                {
                    if (bucket.Active.Remove(record.LeaseId))
                        _ = _RunDestroyAsync(bucket, record);
                }
                _CheckBucketClosed(bucket);
            }
            try { await Task.WhenAll(completions).ConfigureAwait(false); }
            catch (ResourceLoadException)
            {
                // Closed 只在实际操作收尾后完成；已报告并隔离的清理失败不阻断其他资源退出。
            }
        }

        private void _CancelBucketOperations(PoolBucket bucket)
        {
            try { bucket.OperationCts.Cancel(); }
            catch (Exception error)
            {
                // 取消回调失败不能跳过其余实例的销毁；实际创建仍由原完成路径收尾。
                _diagnostics.Report(new LoadError(DiagnosticCodes.ObserverCallbackFailed,
                    LoadStage.Dispose, CleanupStatus.Complete, error, _BucketContext(bucket, "cancel-operations")));
            }
        }

        internal void TickMaintenance(double now)
        {
            foreach (var bucket in _trackedBuckets.ToArray())
            {
                if (bucket.State != PoolState.Open)
                {
                    continue;
                }

                // 销毁到期闲置实例 (FIFO，ReturnedAt 单调)
                var ttl = bucket.Policy.IdleLifetime.TotalSeconds;
                while (bucket.Idle.Count > 0 && now - bucket.Idle.Peek().ReturnedAt >= ttl)
                {
                    _ = _RunDestroyAsync(bucket, bucket.Idle.Dequeue());
                }

                // 无人租用、无等待者、无在途工作且空闲到期的池关闭并释放原型
                if (bucket.Waiters.Count == 0 && bucket.Active.Count == 0 && !bucket.HasInFlight
                    && now - bucket.LastActivity >= ttl)
                {
                    _CloseBucket(bucket);
                }
            }
        }

        internal List<PoolRow> SnapshotRows()
        {
            var list = new List<PoolRow>(_trackedBuckets.Count);
            foreach (var bucket in _trackedBuckets)
            {
                list.Add(new PoolRow(
                    bucket.Key, bucket.Generation, bucket.State,
                    bucket.Active.Count, bucket.Idle.Count,
                    bucket.Creating + bucket.Preparing + bucket.Returning + bucket.Destroying));
            }
            return list;
        }

        #endregion

        #region 私有工具

        /// <summary>
        /// 存活查询三态 (R26)：true=确认存活；false=确认已销毁；null=查询回调抛异常，无法确认。
        /// 查询失败不得当作已销毁——对象已发生所有权转移，按销毁协议清理。
        /// </summary>
        private bool? _TryFactoryIsAlive(PoolBucket bucket, object instance)
        {
            try
            {
                return bucket.FactoryReg.Factory.IsAlive(instance);
            }
            catch (Exception ex)
            {
                _diagnostics.Report(new LoadError(
                    DiagnosticCodes.ObserverCallbackFailed, LoadStage.Instantiate,
                    CleanupStatus.Complete, ex, _BucketContext(bucket, "IsAlive")));
                return null;
            }
        }

        private LoadError _ClassifyFactoryFailure(Exception ex, PoolBucket bucket, string callback)
        {
            if (ex is ResourceLoadException rle)
            {
                return rle.Error;
            }
            return new LoadError(
                DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate,
                CleanupStatus.Unknown, ex, _BucketContext(bucket, callback));
        }

        private LoadError _ResetFailed(PoolBucket bucket, string callback, Exception cause)
        {
            return new LoadError(
                DiagnosticCodes.InstanceResetFailed, LoadStage.Instantiate,
                CleanupStatus.Complete, cause, _BucketContext(bucket, callback));
        }

        private Dictionary<string, object> _BucketContext(PoolBucket bucket, string phase)
        {
            return new Dictionary<string, object>
            {
                { "pool", bucket.Key.ToString() },
                { "generation", bucket.Generation },
                { "phase", phase },
            };
        }

        private static LoadError _ManagerDisposed()
        {
            return new LoadError(DiagnosticCodes.LifecycleManagerDisposed, LoadStage.Dispose,
                CleanupStatus.Complete);
        }

        #endregion
    }

    internal static class DictionaryExtensions
    {
        internal static Dictionary<string, object> Also(
            this Dictionary<string, object> dictionary, Action<Dictionary<string, object>> mutate)
        {
            mutate(dictionary);
            return dictionary;
        }
    }
}
