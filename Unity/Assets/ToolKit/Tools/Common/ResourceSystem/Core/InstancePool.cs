/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 实例池：一个池桶 = 原型引用 + 实例台账；桶不排队、不按额度分配。
 *                有闲置就复用，没有就创建——调用者自己的预算决定等多久，框架不替它排队。
 *                原型由桶聚合持有一份，不随租约数增长；只有实际对象与操作都不再需要原型时才归还。
 *                桶不按时间驻留：没有闲置、没有在用、没有在途操作即退出。
 *                台账是"哪些实例存在"的唯一事实，计数从台账投影，不另存一份会漂移的状态。
 *                ClearPool 只关闭新准入：取走并销毁闲置实例，已接纳的租用照常交付、归还即销毁。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    internal sealed class InstancePool
    {
        private readonly ResourcePipeline _pipeline;
        private readonly Dictionary<string, FactoryRegistration> _factories =
            new Dictionary<string, FactoryRegistration>();
        private readonly Dictionary<PoolKey, PoolBucket> _buckets = new Dictionary<PoolKey, PoolBucket>();
        private readonly object _gate = new object();
        private int _disposed;

        internal InstancePool(ResourcePipeline pipeline)
        {
            _pipeline = pipeline;
        }

        private IExecutionContext Context => _pipeline.Context;

        private IResourceDiagnostics Diagnostics => _pipeline.Diagnostics;

        private double Now() => _pipeline.MonotonicNow();

        #region 工厂注册与路由

        internal void RegisterFactory(string name, IInstanceFactory factory, PoolPolicy? policy)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("工厂名不能为空", nameof(name));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            var snapshot = policy != null ? PoolPolicy.Clone(policy) : new PoolPolicy();
            snapshot.Validate();
            lock (_gate)
            {
                if (_factories.ContainsKey(name))
                    throw new InvalidOperationException($"实例工厂已注册: {name}；不能重复注册");
                _factories.Add(name, new FactoryRegistration(name, factory, snapshot));
            }
        }

        private FactoryRegistration _Route(string name)
        {
            lock (_gate)
            {
                if (_factories.TryGetValue(name, out var registration)) return registration;
            }
            throw new ResourceLoadException(new LoadError(
                DiagnosticCodes.InstanceUnsupported, LoadStage.Instantiate, CleanupStatus.Complete, null,
                new Dictionary<string, object> { { "factory", name } }));
        }

        #endregion

        #region 租用

        /// <summary>
        /// 租用：原型 → 桶 → 复用闲置或直接创建 → 重置并交付租约。全程使用调用者自己的预算。
        /// </summary>
        internal async Task<InstanceLease<T>> RentAsync<T>(
            string factoryName, string address, ResourceSource source, RequestOptions? options,
            CancellationToken callerToken) where T : class
        {
            _ThrowIfDisposed();
            if (string.IsNullOrEmpty(address)) throw new ArgumentException("资源地址不能为空", nameof(address));
            var factory = _Route(factoryName);
            using var budget = _StartBudget(options, callerToken);
            var budgetToken = budget.Token;
            var instanceRequest = new ResourceRequest(source, address, typeof(T), options?.Parameters);
            var prototypeRequest = factory.Factory.GetPrototypeRequest(instanceRequest);
            // 原型来源决定路由与池键：工厂可以指定与实例请求不同的来源。
            var registration = _pipeline.Route(prototypeRequest.Source);
            var resolved = await registration.Loader.ResolveAsync(prototypeRequest, budgetToken)
                .ConfigureAwait(false);
            if (resolved == null || string.IsNullOrEmpty(resolved.LocalKey) || resolved.RepresentationType == null)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "address", prototypeRequest.Address } }));
            }

            var instanceKey = factory.Factory.GetInstanceKey(instanceRequest) ?? "";
            var poolKey = new PoolKey(new ResourceKey(registration.Source, resolved.LocalKey), factory.Name, instanceKey);

            PoolBucket bucket;
            var openNeeded = false;
            lock (_gate)
            {
                if (!_buckets.TryGetValue(poolKey, out bucket!))
                {
                    bucket = new PoolBucket(poolKey, factory.Factory, factory.Policy, this);
                    _buckets.Add(poolKey, bucket);
                    openNeeded = true;
                }
            }

            // 已被清池关闭的旧桶不再接受任何新租用：换一个新桶，旧桶留到自己的收尾结束才退出。
            if (bucket.Closed)
            {
                var replacement = new PoolBucket(poolKey, factory.Factory, factory.Policy, this);
                lock (_gate)
                {
                    _buckets[poolKey] = replacement;
                }
                bucket = replacement;
                openNeeded = true;
            }

            if (openNeeded || !bucket.Opened)
            {
                await _OpenAsync(bucket, registration, resolved, budgetToken).ConfigureAwait(false);
            }

            _ThrowEnded(budget, poolKey.Prototype);

            // 原型值只在状态串行域内读取：它是引擎调用，与引用凭证的访问约束一致。
            var canCreate = Context.Invoke(() => factory.Factory.CanCreate(bucket.PrototypeValue, typeof(T)));
            if (!canCreate)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InstanceUnsupported, LoadStage.Instantiate, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "pool", poolKey.ToString() },
                        { "requestedType", typeof(T).Name },
                    }));
            }

            _ThrowEnded(budget, poolKey.Prototype);

            // 闲置优先复用；没有闲置就直接创建，不在池内排队。
            TrackedInstance? idle = null;
            lock (_gate) bucket.TryTakeIdle(out idle);
            if (idle != null)
            {
                var reused = await _ReuseAsync(bucket, idle, budget).ConfigureAwait(false);
                if (reused is T typed) return new InstanceLease<T>(Context, bucket, typed, idle.InstanceId);
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InstanceUnsupported, LoadStage.Instantiate, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "actualType", reused == null ? "<null>" : reused.GetType().Name },
                        { "requestedType", typeof(T).Name },
                        { "pool", poolKey.ToString() },
                    }));
            }

            // 准入点：发起本次创建即视为已接纳，此后桶关闭不影响这次租用的约定。
            var created = await _CreateAsync(bucket, prototypeRequest, budget.Token).ConfigureAwait(false);
            if (budget.Token.IsCancellationRequested || bucket.Closed)
            {
                // 交付前退出或桶已关闭：实例由本桶按销毁收尾，不进入任何人的持有。
                _ = _DestroyAsync(bucket, created);
                _ThrowEnded(budget, poolKey.Prototype);
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InstancePoolClosed, LoadStage.Instantiate, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "pool", poolKey.ToString() } }));
            }
            if (created is not T instance)
            {
                _ = _DestroyAsync(bucket, created);
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InstanceUnsupported, LoadStage.Instantiate, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "actualType", created.GetType().Name },
                        { "requestedType", typeof(T).Name },
                        { "origin", "pool.create" },
                    }));
            }
            // 交付前把实例调整为可交付状态，然后登记台账：登记之后才算在用。
            await Context.RunAsync(() => factory.Factory.OnRent(created)).ConfigureAwait(false);
            long instanceId;
            lock (_gate) instanceId = bucket.Track(created, InstanceLifecycle.Active);
            return new InstanceLease<T>(Context, bucket, instance, instanceId);
        }

        /// <summary>
        /// 打开池桶：原型引用由桶持有一份，直到桶退出。桶已关闭时立即归还这份引用，不入桶。
        /// </summary>
        private async Task _OpenAsync(
            PoolBucket bucket, LoaderRegistration registration, ResolvedResource resolved,
            CancellationToken budgetToken)
        {
            var start = false;
            lock (_gate)
            {
                if (!bucket.Opening && !bucket.Opened && !bucket.Closed)
                {
                    bucket.Opening = true;
                    start = true;
                }
            }
            if (!start)
            {
                await bucket.OpenedTask.WaitWithCancellation(budgetToken).ConfigureAwait(false);
                return;
            }

            try
            {
                var reference = await _pipeline
                    .AcquireResolvedAsync(registration, resolved, budgetToken).ConfigureAwait(false);
                var keep = false;
                lock (_gate)
                {
                    bucket.Opening = false;
                    if (bucket.Closed)
                    {
                        // 原型在关闭之后才到：不记入桶，由下面立即归还，不留下没有归还者的持有。
                        keep = false;
                    }
                    else
                    {
                        bucket.AttachPrototype(reference);
                        bucket.Opened = true;
                        keep = true;
                    }
                }
                if (!keep) reference.Dispose();
                bucket.MarkOpened();            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    bucket.Opening = false;
                    bucket.Closed = true;
                    _buckets.Remove(bucket.Key);
                }
                bucket.MarkOpened();
                throw error;
            }
        }

        /// <summary> 复用闲置实例：重置在串行域内完成，重置失败只影响本次租用 </summary>
        private async Task<object?> _ReuseAsync(PoolBucket bucket, TrackedInstance idle, Budget budget)
        {
            var instance = idle.Instance;
            try
            {
                await Context.RunAsync(() => bucket.Factory.OnRent(instance)).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var failure = error is ResourceLoadException load ? load.Error : new LoadError(
                    DiagnosticCodes.InstanceResetFailed, LoadStage.Instantiate, CleanupStatus.Unknown, error,
                    new Dictionary<string, object> { { "pool", bucket.Key.ToString() } });
                // 重置失败不再复用：实例走销毁，由桶继续负责到确认结束。
                lock (_gate) idle.Lifecycle = InstanceLifecycle.Retiring;
                NotifyFailure(failure);
                _ = _DestroyAsync(bucket, instance);
                throw new ResourceLoadException(failure);
            }
            if (budget.Token.IsCancellationRequested || bucket.Closed)
            {
                // 交付前退出：实例不进闲置，按销毁收尾。
                lock (_gate) idle.Lifecycle = InstanceLifecycle.Retiring;
                _ = _DestroyAsync(bucket, instance);
                _ThrowEnded(budget, bucket.Key.Prototype);
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InstancePoolClosed, LoadStage.Instantiate, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "pool", bucket.Key.ToString() } }));
            }
            lock (_gate) idle.Lifecycle = InstanceLifecycle.Active;
            return instance;
        }

        /// <summary> 实际创建一次实例；失败与取消都只影响本次租用 </summary>
        private async Task<object> _CreateAsync(
            PoolBucket bucket, ResourceRequest prototypeRequest, CancellationToken budgetToken)
        {
            try
            {
                // 原型是引擎可见对象：创建前在串行域内取值。
                var prototype = Context.Invoke(() => bucket.PrototypeValue);
                var instance = await bucket.Factory
                    .CreateAsync(prototype, prototypeRequest, budgetToken).ConfigureAwait(false);
                if (instance == null)
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate, CleanupStatus.Complete, null,
                        new Dictionary<string, object> { { "pool", bucket.Key.ToString() } }));
                }
                return instance;
            }
            catch (Exception error)
            {
                var failure = error is OperationCanceledException
                    ? new LoadError(DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate,
                        CleanupStatus.Unknown, null,
                        new Dictionary<string, object> { { "pool", bucket.Key.ToString() } })
                    : error is ResourceLoadException load ? load.Error : new LoadError(
                        DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate, CleanupStatus.Unknown, error,
                        new Dictionary<string, object> { { "pool", bucket.Key.ToString() } });
                NotifyFailure(failure);
                if (error is OperationCanceledException) throw;
                throw new ResourceLoadException(failure);
            }
        }

        #endregion

        #region 归还、销毁与闲置维护

        /// <summary>
        /// 归还一个实例。始终投递回状态串行域：销毁回调里也会触发归还，
        /// 同上下文立即归还等于把状态修改嵌进引擎回调。
        /// </summary>
        internal void Return(PoolBucket bucket, object instance, long instanceId)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Context.Post(() => _ReturnCore(bucket, instance, instanceId));
        }

        private void _ReturnCore(PoolBucket bucket, object instance, long instanceId)
        {
            var destroy = false;
            var returnReset = false;
            lock (_gate)
            {
                // 台账以实例身份为准：已经销毁或已被修剪的实例归还只作空转。
                if (!bucket.TryTakeActive(instanceId, out var tracked)) return;
                if (!_IsReusable(bucket, instance))
                {
                    // 引擎已销毁或存活查询失败：撤销台账，不再计一次销毁。
                    // 查询异常不等于确认对象已销毁，这里只按"不可复用"处理。
                    tracked.Lifecycle = InstanceLifecycle.Gone;
                }
                else if (bucket.Closed
                         || bucket.Policy.MaxIdlePerResource <= 0
                         || bucket.IdleCount >= bucket.Policy.MaxIdlePerResource)
                {
                    // 桶已关闭或超出闲置上限：不再闲置，按销毁收尾。
                    tracked.Lifecycle = InstanceLifecycle.Retiring;
                    destroy = true;
                }
                else
                {
                    bucket.ReleaseIdle(tracked, Now());
                    returnReset = true;
                }
            }

            if (returnReset) bucket.Factory.OnReturn(instance);
            if (destroy) _ = _DestroyAsync(bucket, instance);
        }

        /// <summary> 存活查询失败不改变所有权判断，只表示不能复用 </summary>
        private static bool _IsReusable(PoolBucket bucket, object instance)
        {
            try
            {
                return bucket.Factory.IsAlive(instance);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 销毁并确认结束。失败只影响该实例，不由后续查询补救；
        /// 未确认结束时对象仍由本桶负责，桶因此不能退出、原型依赖也不提前消失。
        /// </summary>
        private async Task _DestroyAsync(PoolBucket bucket, object instance)
        {
            lock (_gate) bucket.BeginDestroy(instance);
            LoadError? failure = null;
            try
            {
                await bucket.Factory.DestroyAsync(instance).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure = error is ResourceLoadException load ? load.Error : new LoadError(
                    DiagnosticCodes.InstanceDestroyFailed, LoadStage.DestroyInstance, CleanupStatus.Incomplete,
                    error, new Dictionary<string, object> { { "pool", bucket.Key.ToString() } });
            }
            lock (_gate) bucket.EndDestroy(instance, failure == null);
            if (failure != null) NotifyFailure(failure);
            // 销毁真实结束后桶可能已无任何原型使用者：由维护路径统一判定退出。
            Context.Post(() => _TryClose(bucket));
        }

        /// <summary> 引擎帧或空闲时调用：按闲置寿命修剪实例，并关闭已经无用的桶 </summary>
        internal void Tick()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Context.Invoke(() => _Maintain(Now()));
        }

        private void _Maintain(double now)
        {
            foreach (var bucket in new List<PoolBucket>(_buckets.Values))
            {
                List<object> victims;
                lock (_gate) victims = bucket.TakeExpiredIdle(now, bucket.Policy.IdleLifetime.TotalSeconds);
                foreach (var victim in victims) _ = _DestroyAsync(bucket, victim);
                _TryClose(bucket);
            }
        }

        /// <summary> 桶已无任何原型使用者时关闭并归还原型引用。桶不按时间驻留。 </summary>
        private void _TryClose(PoolBucket bucket)
        {
            lock (_gate)
            {
                if (bucket.Closed || !bucket.Opened) return;
                if (!bucket.CanExit) return;
                bucket.Closed = true;
                _buckets.Remove(bucket.Key);
            }
            bucket.DetachPrototype();
        }

        internal Task ClearPoolAsync(string factoryName, string address, ResourceSource source, CancellationToken ct)
        {
            _ThrowIfDisposed();
            return _ClearPoolAsync(factoryName, address, source, ct);
        }

        /// <summary>
        /// 关闭该地址对应的池桶：关新准入、取走并销毁闲置实例；
        /// 已接纳的租用继续交付，租约归还时直接销毁。
        /// </summary>
        private async Task _ClearPoolAsync(
            string factoryName, string address, ResourceSource source, CancellationToken ct)
        {
            var factory = _Route(factoryName);
            var registration = _pipeline.Route(source);
            var prototypeRequest = factory.Factory.GetPrototypeRequest(
                new ResourceRequest(source, address, typeof(object), null));
            var resolved = await registration.Loader.ResolveAsync(prototypeRequest, ct).ConfigureAwait(false);
            var prototype = new ResourceKey(registration.Source, resolved.LocalKey);

            List<PoolBucket> targets;
            lock (_gate)
            {
                targets = new List<PoolBucket>();
                foreach (var bucket in _buckets.Values)
                {
                    if (bucket.Key.FactoryId == factory.Name && bucket.Key.Prototype.Equals(prototype))
                    {
                        bucket.Closed = true;
                        targets.Add(bucket);
                    }
                }
            }

            foreach (var bucket in targets)
            {
                List<object> idle;
                lock (_gate) idle = bucket.DrainIdle();
                foreach (var instance in idle) await _DestroyAsync(bucket, instance).ConfigureAwait(false);
                // 已接纳的租用与在途创建仍在用原型：这里不归还原型，等它们结束后由维护路径归还。
                _TryClose(bucket);
            }
        }

        internal List<PoolRow> SnapshotRows()
        {
            var rows = new List<PoolRow>();
            lock (_gate)
            {
                foreach (var bucket in _buckets.Values)
                {
                    rows.Add(new PoolRow(bucket.Key,
                        bucket.Closed ? PoolState.Closed : bucket.Opened ? PoolState.Open : PoolState.Opening,
                        bucket.ActiveCount, bucket.IdleCount, bucket.Pending));
                }
            }
            return rows;
        }

        internal async Task DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            List<PoolBucket> buckets;
            lock (_gate)
            {
                buckets = new List<PoolBucket>(_buckets.Values);
                _buckets.Clear();
                foreach (var bucket in buckets) bucket.Closed = true;
            }

            var destroy = new List<Task>();
            foreach (var bucket in buckets)
            {
                List<object> idle;
                lock (_gate) idle = bucket.DrainIdle();
                foreach (var instance in idle) destroy.Add(_DestroyAsync(bucket, instance));
            }
            foreach (var task in destroy) await task.ConfigureAwait(false);
            // 仍在用的实例由各自租约归还；未确认结束的残留继续由桶负责，不被强制释放。
            foreach (var bucket in buckets) bucket.DetachPrototype();
        }

        internal void NotifyFailure(LoadError error) => Diagnostics.Report(error);

        #endregion

        #region 时间预算

        private Budget _StartBudget(RequestOptions? options, CancellationToken callerToken)
        {
            var timeout = options?.Timeout ?? _pipeline.RequestTimeout;
            if (timeout is not TimeSpan limit)
            {
                return new Budget(null, callerToken, _pipeline.MonotonicNow, double.PositiveInfinity);
            }
            var source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            source.CancelAfter(limit);
            return new Budget(
                source, source.Token, _pipeline.MonotonicNow, _pipeline.MonotonicNow() + limit.TotalSeconds);
        }

        /// <summary>
        /// 预算结束的终态：到期是本次请求超时 (ResourceLoadException)，调用者主动取消才是 OperationCanceledException。
        /// 时间预算与个人取消必须区分，否则超时会被业务当成自己的取消处理。
        /// </summary>
        private static void _ThrowEnded(Budget budget, ResourceKey prototype)
        {
            if (budget.TimedOut)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.RequestTimeout, LoadStage.Instantiate, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "key", prototype.ToString() } }));
            }
            budget.Token.ThrowIfCancellationRequested();
        }

        /// <summary> 单次租用的时间预算；跨原型获取、复用与创建，不按阶段重新计时 </summary>
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

            internal bool TimedOut =>
                _deadline > 0 && !double.IsPositiveInfinity(_deadline) && _now() >= _deadline;

            public void Dispose() => _source?.Dispose();
        }

        #endregion

        private void _ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0 || _pipeline.IsDisposed())
                throw new ObjectDisposedException(nameof(ResourceManager));
        }

        private sealed class FactoryRegistration
        {
            internal FactoryRegistration(string name, IInstanceFactory factory, PoolPolicy policy)
            {
                Name = name;
                Factory = factory;
                Policy = policy;
            }

            internal string Name { get; }

            internal IInstanceFactory Factory { get; }

            internal PoolPolicy Policy { get; }
        }

        internal enum InstanceLifecycle
        {
            /// <summary> 在用：已交给调用者的租约 </summary>
            Active,

            /// <summary> 闲置：可再次交付 </summary>
            Idle,

            /// <summary> 正在销毁或已取消交付：不再复用，等待确认结束 </summary>
            Retiring,

            /// <summary> 已经不存在：引擎销毁或已确认销毁完成 </summary>
            Gone,
        }

        internal sealed class TrackedInstance
        {
            internal TrackedInstance(object instance, long instanceId)
            {
                Instance = instance;
                InstanceId = instanceId;
            }

            internal object Instance { get; }

            internal long InstanceId { get; }

            internal InstanceLifecycle Lifecycle { get; set; } = InstanceLifecycle.Active;

            internal double ReturnedAt { get; set; }
        }

        /// <summary>
        /// 一个池桶：原型引用与实例台账。台账是实例身份的唯一事实：
        /// 连续两次归还同一个实例只生效一次。
        /// </summary>
        internal sealed class PoolBucket
        {
            private readonly TaskCompletionSource<bool> _opened =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Dictionary<long, TrackedInstance> _instances = new Dictionary<long, TrackedInstance>();
            private ResourceRef<object>? _prototype;
            private long _instanceSeed;

            internal PoolBucket(PoolKey key, IInstanceFactory factory, PoolPolicy policy, InstancePool owner)
            {
                Key = key;
                Factory = factory;
                Policy = policy;
                Owner = owner;
            }

            internal PoolKey Key { get; }

            internal IInstanceFactory Factory { get; }

            internal PoolPolicy Policy { get; }

            internal InstancePool Owner { get; }

            internal bool Opened { get; set; }

            internal bool Opening { get; set; }

            internal bool Closed { get; set; }

            /// <summary> 尚未确认结束的销毁数 </summary>
            internal int Pending { get; private set; }

            internal Task OpenedTask => _opened.Task;

            internal object PrototypeValue => _prototype!.Value;

            /// <summary> 在用实例数：从台账投影，不另存计数 </summary>
            internal int ActiveCount => _Count(InstanceLifecycle.Active);

            internal int IdleCount => _Count(InstanceLifecycle.Idle);

            /// <summary>
            /// 仍有对象或操作需要这份原型：在用、闲置、回收中的实例与在途销毁都算。
            /// 复用的实例在取下时就转为在用，创建中的实例在交付前先确认桶是否已关闭，
            /// 因此不需要再单独记一份"已接纳"计数。
            /// </summary>
            internal bool CanExit =>
                _prototype != null && ActiveCount == 0 && IdleCount == 0
                && _Count(InstanceLifecycle.Retiring) == 0 && Pending == 0;

            private int _Count(InstanceLifecycle lifecycle)
            {
                var count = 0;
                foreach (var tracked in _instances.Values)
                {
                    if (tracked.Lifecycle == lifecycle) count++;
                }
                return count;
            }

            internal void MarkOpened() => _opened.TrySetResult(true);
            internal void AttachPrototype(ResourceRef<object> reference) => _prototype = reference;

            internal void DetachPrototype()
            {
                var prototype = _prototype;
                _prototype = null;
                prototype?.Dispose();
            }

            /// <summary> 创建完成的实例进入台账，并把它交给调用者 </summary>
            internal long Track(object instance, InstanceLifecycle lifecycle)
            {
                var id = ++_instanceSeed;
                _instances.Add(id, new TrackedInstance(instance, id) { Lifecycle = lifecycle });
                return id;
            }

            /// <summary> 归还：只对仍然在用的那份实例生效 </summary>
            internal bool TryTakeActive(long instanceId, out TrackedInstance tracked)
            {
                tracked = null!;
                if (!_instances.TryGetValue(instanceId, out var found)) return false;
                if (found.Lifecycle != InstanceLifecycle.Active) return false;
                tracked = found;
                return true;
            }

            internal void ReleaseIdle(TrackedInstance tracked, double now)
            {
                tracked.Lifecycle = InstanceLifecycle.Idle;
                tracked.ReturnedAt = now;
            }

            internal bool TryTakeIdle(out TrackedInstance? tracked)
            {
                tracked = null;
                foreach (var candidate in _instances.Values)
                {
                    if (candidate.Lifecycle != InstanceLifecycle.Idle) continue;
                    tracked = candidate;
                    break;
                }
                if (tracked == null) return false;
                tracked.Lifecycle = InstanceLifecycle.Active;
                return true;
            }

            internal List<object> TakeExpiredIdle(double now, double lifetimeSeconds)
            {
                List<object>? victims = null;
                foreach (var tracked in _instances.Values)
                {
                    if (tracked.Lifecycle != InstanceLifecycle.Idle) continue;
                    if (now - tracked.ReturnedAt < lifetimeSeconds) continue;
                    tracked.Lifecycle = InstanceLifecycle.Retiring;
                    (victims ??= new List<object>()).Add(tracked.Instance);
                }
                return victims ?? new List<object>();
            }

            internal List<object> DrainIdle()
            {
                var drained = new List<object>();
                foreach (var tracked in _instances.Values)
                {
                    if (tracked.Lifecycle != InstanceLifecycle.Idle) continue;
                    tracked.Lifecycle = InstanceLifecycle.Retiring;
                    drained.Add(tracked.Instance);
                }
                return drained;
            }

            internal void BeginDestroy(object instance)
            {
                Pending++;
                foreach (var tracked in _instances.Values)
                {
                    if (ReferenceEquals(tracked.Instance, instance)) tracked.Lifecycle = InstanceLifecycle.Retiring;
                }
            }

            /// <summary>
            /// 销毁结束：确认完成才把实例移出台账。
            /// 未确认结束时保留台账项与原型依赖——桶因此不能退出，这是这项保证的成本。
            /// </summary>
            internal void EndDestroy(object instance, bool confirmed)
            {
                if (Pending > 0) Pending--;
                if (!confirmed)
                {
                    // 未确认结束：对象可能仍存活，保留在台账里等待后续确认，不假装释放成功。
                    foreach (var tracked in _instances.Values)
                    {
                        if (ReferenceEquals(tracked.Instance, instance)) tracked.Lifecycle = InstanceLifecycle.Gone;
                    }
                    return;
                }
                List<long>? removed = null;
                foreach (var pair in _instances)
                {
                    if (!ReferenceEquals(pair.Value.Instance, instance)) continue;
                    (removed ??= new List<long>()).Add(pair.Key);
                }
                if (removed == null) return;
                foreach (var id in removed) _instances.Remove(id);
            }
        }
    }

    /// <summary>
    /// 实例租约：只持有和归还一个实例，不调度创建或销毁。
    /// 归还始终投递回状态串行域 —— 归还可能来自实例销毁回调，
    /// 同上下文立即归还等于把状态修改嵌进引擎回调。
    /// </summary>
    public sealed class InstanceLease<T> : IDisposable where T : class
    {
        private readonly IExecutionContext _context;
        private readonly InstancePool.PoolBucket _bucket;
        private readonly T _value;
        private readonly long _instanceId;
        private int _disposed;

        internal InstanceLease(IExecutionContext context, InstancePool.PoolBucket bucket, T value, long instanceId)
        {
            _context = context;
            _bucket = bucket;
            _value = value;
            _instanceId = instanceId;
        }

        /// <summary> 实例对象；在状态串行域外访问立即失败 </summary>
        public T Value
        {
            get
            {
                _context.AssertAccess();
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ResourceLoadException(_Invalidated());
                }
                return _value;
            }
        }

        /// <summary> 该租约是否仍然有效；不触碰引擎对象 </summary>
        public bool IsValid => Volatile.Read(ref _disposed) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _bucket.Owner.Return(_bucket, _value, _instanceId);
        }

        private LoadError _Invalidated() => new LoadError(
            DiagnosticCodes.InstanceUnsupported, LoadStage.ReturnInstance, CleanupStatus.Complete, null,
            new Dictionary<string, object> { { "pool", _bucket.Key.ToString() } });
    }
}
