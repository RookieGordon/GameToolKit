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
    /// <summary> 租用等待者：独立状态与取消，只转换一次 </summary>
    internal sealed class RentWaiter
    {
        public readonly long Id;
        public WaiterState State;
        public readonly Type RequestedType;
        public readonly TaskCompletionSource<object> Completion;
        public readonly CancellationToken CallerToken;
        public readonly Func<PoolBucket, InstanceRecord, object> LeaseFactory;
        public CancellationTokenRegistration Registration;

        public RentWaiter(
            long id,
            Type requestedType,
            CancellationToken callerToken,
            Func<PoolBucket, InstanceRecord, object> leaseFactory)
        {
            Id = id;
            RequestedType = requestedType;
            CallerToken = callerToken;
            LeaseFactory = leaseFactory;
            Completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary> 实例记录：一个实例在桶内的生命周期账目 </summary>
    internal sealed class InstanceRecord
    {
        public readonly long LeaseId;
        public readonly object Value;
        public InstanceState State;
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
        public readonly string AssociationLoader;
        public readonly string AssociationAddress;
        public readonly string AssociationFactory;
        /// <summary> 全部有效业务请求关联 (loader|address|factory)：工厂可将原型映射到其他加载器 (R33) </summary>
        public readonly HashSet<string> Associations = new HashSet<string>(StringComparer.Ordinal);
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
        public LoadError? StoredFault;

        public PoolBucket(
            PoolKey key,
            long generation,
            FactoryRegistration factoryReg,
            PoolPolicy policy,
            ResourceRequest creationRequest,
            string associationLoader,
            string associationAddress,
            string associationFactory)
        {
            Key = key;
            Generation = generation;
            FactoryReg = factoryReg;
            Policy = policy;
            CreationRequest = creationRequest;
            AssociationLoader = associationLoader;
            AssociationAddress = associationAddress;
            AssociationFactory = associationFactory;
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
        private readonly Func<bool> _isAcceptingNewRequests;
        private readonly Action _checkShutdownComplete;

        private readonly Dictionary<PoolKey, PoolBucket> _currentBuckets = new Dictionary<PoolKey, PoolBucket>();
        private readonly List<PoolBucket> _trackedBuckets = new List<PoolBucket>(); // 含退役桶，直到 Closed/Faulted 终局
        /// <summary> 同 PoolKey 清理故障屏障：独立于当前桶索引，阻止新租用 (R27) </summary>
        private readonly Dictionary<PoolKey, LoadError> _faultBarriers = new Dictionary<PoolKey, LoadError>();
        private long _generationSeed;
        private long _waiterSeed;
        private long _recordSeed;

        internal static string AssociationKey(string loader, string address, string factory)
        {
            return loader + "|" + address + "|" + factory;
        }

        internal InstancePool(
            IExecutionContext context,
            LoadManager loads,
            IResourceDiagnostics diagnostics,
            Func<double> monotonicNow,
            PoolPolicy defaultPolicy,
            Func<bool> isAcceptingNewRequests,
            Action checkShutdownComplete)
        {
            _context = context;
            _loads = loads;
            _diagnostics = diagnostics;
            _monotonicNow = monotonicNow;
            _defaultPolicy = defaultPolicy;
            _isAcceptingNewRequests = isAcceptingNewRequests;
            _checkShutdownComplete = checkShutdownComplete;
        }

        internal IExecutionContext Context => _context;

        #region 租用入口

        /// <summary>
        /// 全程在上下文内。桶不存在则登记 Initializing 桶并启动原型获取 (固定 registration/resolved 快照)；
        /// Faulted 桶直接返回其清理故障。返回该调用者的独立任务。
        /// </summary>
        internal Task<object> JoinOrRent(
            PoolKey poolKey,
            FactoryRegistration factoryReg,
            LoaderRegistration protoRegistration,
            ResolvedResource protoResolved,
            ResourceRequest instanceRequest,
            Type requestedType,
            Func<PoolBucket, InstanceRecord, object> leaseFactory,
            CancellationToken callerCt)
        {
            if (!_isAcceptingNewRequests())
            {
                throw new ResourceLoadException(_ManagerClosing());
            }
            callerCt.ThrowIfCancellationRequested();

            // 同 PoolKey 清理故障屏障优先于任何当前桶 (R27)：旧代际隔离后新代际同样拒绝租用
            if (_faultBarriers.TryGetValue(poolKey, out var barrier))
            {
                throw new ResourceLoadException(barrier);
            }

            if (_currentBuckets.TryGetValue(poolKey, out var bucket))
            {
                if (bucket.State == PoolState.Faulted)
                {
                    throw new ResourceLoadException(bucket.StoredFault!);
                }
                // 同一池可由多个业务地址到达：登记本请求的关联 (R33)
                bucket.Associations.Add(AssociationKey(instanceRequest.LoaderId, instanceRequest.Address, factoryReg.Name));
                var task = _AddWaiter(bucket, requestedType, leaseFactory, callerCt);
                _Pump(bucket); // 已开放桶的新等待者立即尝试交付 (闲置实例或触发创建)
                return task;
            }

            bucket = new PoolBucket(
                poolKey,
                Interlocked.Increment(ref _generationSeed),
                factoryReg,
                factoryReg.Policy ?? _defaultPolicy,
                instanceRequest,
                instanceRequest.LoaderId, // 业务请求的加载器关联，而非工厂映射后的原型加载器 (R33)
                instanceRequest.Address,
                factoryReg.Name)
            {
                State = PoolState.Initializing,
                LastActivity = _monotonicNow(),
            };
            bucket.Associations.Add(AssociationKey(instanceRequest.LoaderId, instanceRequest.Address, factoryReg.Name));
            _currentBuckets[poolKey] = bucket;
            _trackedBuckets.Add(bucket);
            var waiterTask = _AddWaiter(bucket, requestedType, leaseFactory, callerCt);
            // 原型获取在状态登记之后启动，等待同键屏障后继续使用同一 resolved 快照
            _ = _InitializeBucketAsync(bucket, protoRegistration, protoResolved);
            return waiterTask;
        }

        private Task<object> _AddWaiter(
            PoolBucket bucket,
            Type requestedType,
            Func<PoolBucket, InstanceRecord, object> leaseFactory,
            CancellationToken callerCt)
        {
            var w = new RentWaiter(Interlocked.Increment(ref _waiterSeed), requestedType, callerCt, leaseFactory);
            bucket.Waiters.Add(w);
            w.Registration = callerCt.Register(() => _context.Post(() => _CancelWaiter(bucket, w)));
            return w.Completion.Task;
        }

        private void _CancelWaiter(PoolBucket bucket, RentWaiter w)
        {
            if (w.State != WaiterState.Pending)
            {
                return;
            }
            w.State = WaiterState.Cancelled;
            bucket.Waiters.Remove(w);
            w.Completion.TrySetCanceled();
        }

        private void _FailWaiter(PoolBucket bucket, RentWaiter w, LoadError error)
        {
            if (w.State != WaiterState.Pending)
            {
                return;
            }
            w.State = WaiterState.Failed;
            bucket.Waiters.Remove(w);
            w.Completion.TrySetException(new ResourceLoadException(error));
        }

        private async Task _InitializeBucketAsync(
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
            bucket.InitializationPending = false;

            if (bucket.State == PoolState.Closing || bucket.State == PoolState.Closed)
            {
                prototype?.Dispose();
                _CheckBucketClosed(bucket);
                return;
            }

            if (error != null)
            {
                // 原型加载失败：按清理状态分类；未分类异常无法确认回退，保守隔离
                var loadError = error is OperationCanceledException && bucket.OperationCts.Token.IsCancellationRequested
                    ? null // 正常取消：等待者按各自令牌取消，不发故障日志
                    : error is ResourceLoadException rle
                        ? rle.Error
                        : new LoadError(DiagnosticCodes.InternalUnexpected, LoadStage.LoadAsset,
                            CleanupStatus.Unknown, error, _BucketContext(bucket, "prototype"));
                if (loadError == null)
                {
                    foreach (var w in bucket.Waiters.ToArray())
                    {
                        _CancelWaiter(bucket, w);
                    }
                    _FinishInitialization(bucket, prototype, report: null);
                    return;
                }
                foreach (var w in bucket.Waiters.ToArray())
                {
                    if (w.CallerToken.IsCancellationRequested)
                    {
                        _CancelWaiter(bucket, w);
                    }
                    else
                    {
                        _FailWaiter(bucket, w, loadError);
                    }
                }
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
                foreach (var w in bucket.Waiters.ToArray())
                {
                    if (w.CallerToken.IsCancellationRequested)
                    {
                        _CancelWaiter(bucket, w);
                    }
                    else
                    {
                        _FailWaiter(bucket, w, failure);
                    }
                }
                _FinishInitialization(bucket, prototype, report: failure);
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
            _checkShutdownComplete();
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
                        record.State = InstanceState.Destroyed;
                        continue;
                    }
                    if (alive == null)
                    {
                        // 存活查询失败：保守销毁，不得当作已销毁交付 (R26)
                        _TransferToDestroying(bucket, record);
                        continue;
                    }

                    record.State = InstanceState.Preparing;
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
                        _TransferToDestroying(bucket, record);
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
                        _TransferToDestroying(bucket, record);
                        continue;
                    }

                    // 最终交付前再次检查 manager、桶和等待者
                    if (!_isAcceptingNewRequests() || bucket.State != PoolState.Open
                        || waiter.State != WaiterState.Pending || waiter.CallerToken.IsCancellationRequested)
                    {
                        bucket.Preparing--;
                        if (waiter.State == WaiterState.Pending && waiter.CallerToken.IsCancellationRequested)
                        {
                            _CancelWaiter(bucket, waiter);
                        }
                        _TransferToDestroying(bucket, record);
                        continue;
                    }

                    bucket.Preparing--;
                    record.State = InstanceState.Active;
                    bucket.Active[record.LeaseId] = record;
                    waiter.State = WaiterState.Granted;
                    bucket.Waiters.Remove(waiter);
                    var lease = waiter.LeaseFactory(bucket, record);
                    bucket.LastActivity = _monotonicNow();
                    waiter.Completion.TrySetResult(lease); // 先记录租约，再完成等待者
                }
                else if (bucket.Creating == 0)
                {
                    _StartCreate(bucket);
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
                if (head.State == WaiterState.Pending && !head.CallerToken.IsCancellationRequested)
                {
                    return head;
                }
                if (head.State == WaiterState.Pending)
                {
                    _CancelWaiter(bucket, head);
                    continue;
                }
                bucket.Waiters.RemoveAt(0); // 已终局未清理的尾巴
            }
            return null;
        }

        private bool _QuotaAvailable(PoolBucket bucket)
        {
            var max = bucket.Policy.MaxActivePerResource;
            return max == null || bucket.Active.Count + bucket.Preparing + bucket.Creating < max.Value;
        }

        private void _StartCreate(PoolBucket bucket)
        {
            bucket.Creating = 1;
            bucket.LastActivity = _monotonicNow();
            var factory = bucket.FactoryReg.Factory;
            try
            {
                var prototypeValue = bucket.Prototype!.Value; // 上下文内访问
                var createTask = factory.CreateAsync(prototypeValue, bucket.CreationRequest, bucket.OperationCts.Token);
                _ = _RunCreateAsync(bucket, createTask);
            }
            catch (Exception ex)
            {
                // 同步抛出与任务失败走同一路径，不能让 Creating 卡死或异常穿透状态提交
                _OnCreateCompleted(bucket, null, ex);
            }
        }

        private async Task _RunCreateAsync(PoolBucket bucket, Task<object> createTask)
        {
            object? instance = null;
            Exception? error = null;
            try
            {
                instance = await createTask.ConfigureAwait(false);
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
            bucket.Creating = 0;

            // 失败结果与清理语义优先处理：关闭不改变失败回退的责任 (R07)
            if (error != null)
            {
                var err = _ClassifyFactoryFailure(error, bucket, "CreateAsync");
                if (err.Cleanup != CleanupStatus.Complete)
                {
                    _FaultBucket(bucket, err);
                    return;
                }
                // 清理工厂未交付对象的责任在工厂；失败队首有效等待者后继续，避免无限重试
                var waiter = _FirstPendingWaiter(bucket);
                if (waiter != null)
                {
                    _FailWaiter(bucket, waiter, err);
                }
                _diagnostics.Report(err);
                _Pump(bucket); // 桶非 Open 时 Pump 无操作，由关闭路径收尾
                return;
            }

            if (instance != null)
            {
                var alive = _TryFactoryIsAlive(bucket, instance);
                if (alive != true)
                {
                    // 先结算等待者，再安排销毁：同步销毁回调重入 Pump 时不再看到待交付请求 (R26)
                    var record = new InstanceRecord(Interlocked.Increment(ref _recordSeed), instance);
                    var waiter = _FirstPendingWaiter(bucket);
                    if (waiter != null)
                    {
                        _FailWaiter(bucket, waiter, new LoadError(
                            DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate,
                            CleanupStatus.Complete, null,
                            _BucketContext(bucket, alive == null ? "CreateAsync:alive-query-failed" : "CreateAsync:null-result")));
                    }
                    if (alive == null)
                    {
                        // 存活查询失败：保留记录按销毁协议清理，不当作已销毁
                        _TransferToDestroying(bucket, record);
                    }
                    else
                    {
                        record.State = InstanceState.Destroyed;
                    }
                    _Pump(bucket);
                    return;
                }
            }
            else
            {
                var waiter = _FirstPendingWaiter(bucket);
                if (waiter != null)
                {
                    _FailWaiter(bucket, waiter, new LoadError(
                        DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate,
                        CleanupStatus.Complete, null, _BucketContext(bucket, "CreateAsync:null-result")));
                }
                _Pump(bucket);
                return;
            }

            if (bucket.State != PoolState.Open || !_isAcceptingNewRequests())
            {
                _DestroyNewInstance(bucket, instance);
                _CheckBucketClosed(bucket);
                return;
            }

            var created = new InstanceRecord(Interlocked.Increment(ref _recordSeed), instance);
            _DeliverCreated(bucket, created);
            _Pump(bucket);
        }

        private void _DeliverCreated(PoolBucket bucket, InstanceRecord record)
        {
            var waiter = _FirstPendingWaiter(bucket);
            if (waiter == null)
            {
                // 无人等待：与普通归还同一状态流 (Returning 保护 + 回调后重查)，
                // 不得使用缺少记账保护的旁路 (R08)
                _ReturnRecord(bucket, record);
                return;
            }
            // 作为未交付候选进入闲置队列，由 Pump 执行 OnRent 并交付
            record.State = InstanceState.Idle;
            bucket.Idle.Enqueue(record);
        }

        private void _DestroyNewInstance(PoolBucket bucket, object instance)
        {
            var record = new InstanceRecord(Interlocked.Increment(ref _recordSeed), instance);
            _TransferToDestroying(bucket, record);
        }

        #endregion

        #region 归还、销毁与桶状态 (§7.3-§7.4)

        /// <summary> 归还租约记录的原池；必须通过 lease 一次性门闩进入 </summary>
        internal void Return(PoolBucket bucket, InstanceRecord record)
        {
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
            bucket.LastActivity = _monotonicNow();

            var alive = _TryFactoryIsAlive(bucket, record.Value);
            if (alive == false)
            {
                record.State = InstanceState.Destroyed;
                _Pump(bucket);
                _CheckBucketClosed(bucket);
                return;
            }
            if (alive == null)
            {
                // 无法确认存活：保留记录按销毁协议清理
                _TransferToDestroying(bucket, record);
                _Pump(bucket);
                _CheckBucketClosed(bucket);
                return;
            }

            if (bucket.State == PoolState.Open && _isAcceptingNewRequests()
                && bucket.Idle.Count < bucket.Policy.MaxIdlePerResource)
            {
                record.State = InstanceState.Returning;
                bucket.Returning++;
                try
                {
                    bucket.FactoryReg.Factory.OnReturn(record.Value);
                }
                catch (Exception ex)
                {
                    bucket.Returning--;
                    _TransferToDestroying(bucket, record);
                    _diagnostics.Report(_ResetFailed(bucket, "OnReturn", ex));
                    _Pump(bucket);
                    _CheckBucketClosed(bucket);
                    return;
                }
                bucket.Returning--;
                // 回调后再次检查 manager、桶和容量 (回调可能重入 ClearPool)
                if (bucket.State != PoolState.Open || !_isAcceptingNewRequests()
                    || bucket.Idle.Count >= bucket.Policy.MaxIdlePerResource)
                {
                    _TransferToDestroying(bucket, record);
                    _Pump(bucket);
                    _CheckBucketClosed(bucket);
                    return;
                }
                record.State = InstanceState.Idle;
                record.ReturnedAt = _monotonicNow();
                bucket.Idle.Enqueue(record);
                _Pump(bucket);
                return;
            }

            _TransferToDestroying(bucket, record);
            _Pump(bucket);
        }

        private void _TransferToDestroying(PoolBucket bucket, InstanceRecord record)
        {
            record.State = InstanceState.Destroying;
            bucket.Destroying++;
            _ = _RunDestroyAsync(bucket, record);
        }

        private async Task _RunDestroyAsync(PoolBucket bucket, InstanceRecord record)
        {
            Exception? error = null;
            try
            {
                await bucket.FactoryReg.Factory.DestroyAsync(record.Value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            _context.Post(() =>
            {
                if (error == null)
                {
                    bucket.Destroying--;
                    record.State = InstanceState.Destroyed;
                    _Pump(bucket);
                    _CheckBucketClosed(bucket);
                }
                else
                {
                    var err = new LoadError(
                        DiagnosticCodes.InstanceDestroyFailed, LoadStage.DestroyInstance,
                        CleanupStatus.Incomplete, error,
                        _BucketContext(bucket, "DestroyAsync").Also(c => c.Add("record", record.LeaseId)));
                    if (bucket.State == PoolState.Faulted)
                    {
                        // 已隔离的桶不再重复故障，仅追加诊断
                        _diagnostics.Report(err);
                    }
                    else
                    {
                        _FaultBucket(bucket, err);
                    }
                }
            });
        }

        private void _CloseBucket(PoolBucket bucket)
        {
            if (bucket.State == PoolState.Closing || bucket.State == PoolState.Closed
                || bucket.State == PoolState.Faulted)
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
            foreach (var w in bucket.Waiters.ToArray())
            {
                if (w.CallerToken.IsCancellationRequested)
                {
                    _CancelWaiter(bucket, w);
                }
                else
                {
                    _FailWaiter(bucket, w, closed);
                }
            }

            // 取消尚未开始的创建；已开始的创建仍负责观察并清理结果
            bucket.OperationCts.Cancel();
            while (bucket.Idle.Count > 0)
            {
                _TransferToDestroying(bucket, bucket.Idle.Dequeue());
            }
            _CheckBucketClosed(bucket);
        }

        private void _CheckBucketClosed(PoolBucket bucket)
        {
            if (bucket.State != PoolState.Closing)
            {
                return;
            }
            if (bucket.InitializationPending || !bucket.IsEmptyOfInstances || bucket.HasInFlight)
            {
                return;
            }
            bucket.Prototype?.Dispose(); // 归还框架资源持有，物理卸载由 LoadManager 安排
            bucket.Prototype = null;
            bucket.State = PoolState.Closed;
            _RemoveBucket(bucket);
            bucket.OperationCts.Dispose();
            bucket.Closed.TrySetResult(null!);
            _checkShutdownComplete();
        }

        private void _FaultBucket(PoolBucket bucket, LoadError error)
        {
            bucket.State = PoolState.Faulted;
            bucket.StoredFault = error;
            // 同 PoolKey 清理故障屏障独立登记 (R27)：即使当前索引已被新代际占据，
            // 后续租用也直接返回本清理故障；同键新桶的待交付工作一并终结
            _faultBarriers[bucket.Key] = error;
            if (_currentBuckets.TryGetValue(bucket.Key, out var occupying)
                && !ReferenceEquals(occupying, bucket))
            {
                _CloseBucket(occupying);
            }
            _currentBuckets.TryAdd(bucket.Key, bucket);

            foreach (var w in bucket.Waiters.ToArray())
            {
                if (w.CallerToken.IsCancellationRequested)
                {
                    _CancelWaiter(bucket, w);
                }
                else
                {
                    _FailWaiter(bucket, w, error);
                }
            }

            // 仍可安全销毁的闲置实例安排销毁；已知活跃实例仍接受归还
            while (bucket.Idle.Count > 0)
            {
                _TransferToDestroying(bucket, bucket.Idle.Dequeue());
            }
            // 先提交状态与终局，再做诊断通知：观察者故障不得中断生命周期 (R24)
            bucket.Closed.TrySetException(new ResourceLoadException(error));
            _diagnostics.Report(error);
            _checkShutdownComplete();
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
        /// 关闭在调用时捕获的、与业务请求 (loader|address|factory) 关联的全部池代际 (R33)：
        /// 关联保存原始业务请求，工厂将原型映射到其他加载器时同样可以按原参数清池。
        /// </summary>
        internal List<Task> CloseBuckets(string address, string loader, string factory)
        {
            var association = AssociationKey(loader, address, factory);
            var tasks = new List<Task>();
            foreach (var bucket in _trackedBuckets.ToArray())
            {
                if (bucket.Associations.Contains(association)
                    || bucket.AssociationAddress == address
                    && bucket.AssociationLoader == loader
                    && bucket.AssociationFactory == factory)
                {
                    tasks.Add(bucket.Closed.Task);
                    _CloseBucket(bucket);
                }
            }
            return tasks;
        }

        internal void BeginClose()
        {
            foreach (var bucket in _trackedBuckets.ToArray())
            {
                _CloseBucket(bucket);
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
                    _TransferToDestroying(bucket, bucket.Idle.Dequeue());
                }

                // 无人租用、无等待者、无在途工作且空闲到期的池关闭并释放原型
                if (bucket.Waiters.Count == 0 && bucket.Active.Count == 0 && !bucket.HasInFlight
                    && now - bucket.LastActivity >= ttl)
                {
                    _CloseBucket(bucket);
                }
            }
        }

        /// <summary> 没有仍在飞行 (Initializing/Open/Closing) 的桶；Faulted 桶视为卡滞而非飞行 </summary>
        internal bool IsQuiesced
        {
            get
            {
                foreach (var bucket in _trackedBuckets)
                {
                    if (bucket.State != PoolState.Faulted)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        internal bool AllClosed => _trackedBuckets.Count == 0;

        internal List<LoadError> CollectStuckErrors()
        {
            var list = new List<LoadError>();
            foreach (var bucket in _trackedBuckets)
            {
                if (bucket.State == PoolState.Faulted && bucket.StoredFault != null)
                {
                    list.Add(bucket.StoredFault);
                }
            }
            return list;
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

        internal List<string> DescribeOutstanding()
        {
            var list = new List<string>();
            foreach (var bucket in _trackedBuckets)
            {
                if (bucket.Active.Count > 0)
                {
                    list.Add($"{bucket.Key} gen={bucket.Generation} active={bucket.Active.Count}");
                }
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

        private static LoadError _ManagerClosing()
        {
            return new LoadError(DiagnosticCodes.LifecycleManagerClosing, LoadStage.Shutdown,
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
