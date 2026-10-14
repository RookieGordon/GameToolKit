/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 实例池：一个池桶 = 原型引用 + 实例台账 + 等待者。
 *                原型由资源流程按身份取得，桶不重新解析地址；每桶同时最多一次创建，等待者 FIFO；
 *                活跃配额 = 台账中在用的实例数 + 创建中；创建与销毁都必须观察到真实结束。
 *                原型不得提前归还：有闲置、在用或创建/销毁进行中时都不释放。
 *                实例台账是"哪些实例存在"的唯一事实，计数从台账投影，不另存一份会漂移的状态。
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
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
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

        internal async Task<InstanceLease<T>> RentAsync<T>(
            string factoryName, string address, ResourceSource source, RequestOptions? options,
            CancellationToken callerToken) where T : class
        {
            _ThrowIfDisposed();
            if (string.IsNullOrEmpty(address)) throw new ArgumentException("资源地址不能为空", nameof(address));
            var factory = _Route(factoryName);
            var registration = _pipeline.Route(source);
            var instanceRequest = new ResourceRequest(source, address, typeof(T), options?.Parameters);
            var prototypeRequest = factory.Factory.GetPrototypeRequest(instanceRequest);
            // 先解析一次，池键固定在这一版原型描述上；等待旧记录结束时继续用同一份描述。
            var resolved = await registration.Loader.ResolveAsync(prototypeRequest, callerToken)
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
                    bucket = new PoolBucket(poolKey, factory.Factory, factory.Policy, this, Now());
                    _buckets.Add(poolKey, bucket);
                    openNeeded = true;
                }
            }

            if (openNeeded || !bucket.Opened)
            {
                await _OpenAsync(bucket, registration, resolved, callerToken).ConfigureAwait(false);
            }
            if (bucket.Closed)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InstancePoolClosed, LoadStage.Instantiate, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "pool", poolKey.ToString() } }));
            }
            // 原型值只在串行域内读取：它是引擎调用，与引用凭证的访问约束一致。
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

            var request = new RentRequest<T>(bucket, instanceRequest, callerToken);
            PumpResult? next;
            lock (_gate)
            {
                bucket.Queue.Add(request);
                next = _Pump(bucket);
            }
            _RunPumpResult(bucket, next);

            var instance = await request.Waiter.Task.ConfigureAwait(false);
            return new InstanceLease<T>(Context, bucket, instance, request.InstanceId);
        }

        /// <summary> 推进一次等待者：闲置实例优先复用，否则开始一次实际创建 </summary>
        private void _RunPumpResult(PoolBucket bucket, PumpResult? next)
        {
            if (next is not PumpResult result) return;
            if (result.Idle != null) _ = _ReuseIdleAsync(bucket, result.Request, result.Idle);
            else _ = _CreateAsync(bucket, result.Request);
        }

        /// <summary> 复用闲置实例：重置在串行域内完成，重置失败只影响本次租用 </summary>
        private async Task _ReuseIdleAsync(PoolBucket bucket, RentRequest request, object instance)
        {
            request.MarkStarted();
            try
            {
                await Context.RunAsync(() => bucket.Factory.OnRent(instance)).ConfigureAwait(false);
                _CompleteReuse(bucket, request, instance);
            }
            catch (Exception error)
            {
                var failure = error is ResourceLoadException load ? load.Error : new LoadError(
                    DiagnosticCodes.InstanceResetFailed, LoadStage.Instantiate, CleanupStatus.Unknown, error,
                    new Dictionary<string, object> { { "pool", bucket.Key.ToString() } });
                // 重置失败的实例必须销毁，不能带着半初始化状态回到闲置队列。
                lock (_gate) request.MarkFinished();
                request.Fail(failure);
                _ = _DestroyAsync(bucket, instance);
            }
        }

        private void _CompleteReuse(PoolBucket bucket, RentRequest request, object instance)
        {
            lock (_gate)
            {
                request.MarkFinished();
                if (request.RequestToken.IsCancellationRequested || bucket.Closed)
                {
                    _ = _DestroyAsync(bucket, instance);
                    return;
                }
                request.Succeed(instance, bucket.Track(instance, InstanceLifecycle.Active));
            }
            _Advance(bucket);
        }

        /// <summary> 打开池桶：原型引用由桶持有到关闭，之后不再重新解析地址 </summary>
        private async Task _OpenAsync(
            PoolBucket bucket, LoaderRegistration registration, ResolvedResource resolved,
            CancellationToken callerToken)
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
                await bucket.OpenedTask.WaitWithCancellation(callerToken).ConfigureAwait(false);
                return;
            }

            try
            {
                var reference = await _pipeline
                    .AcquireResolvedAsync(registration, resolved, callerToken).ConfigureAwait(false);
                lock (_gate)
                {
                    bucket.AttachPrototype(reference);
                    bucket.Opened = true;
                    bucket.Opening = false;
                }
                bucket.MarkOpened();
            }
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

        #endregion

        #region 桶推进：最多一次创建 + 等待者 FIFO

        /// <summary> 取下一个可以推进的等待者：闲置实例优先复用，否则开始一次创建 </summary>
        private PumpResult? _Pump(PoolBucket bucket)
        {
            if (bucket.Closed) return null;
            while (bucket.Queue.Count > 0)
            {
                var request = bucket.Queue[0];
                bucket.Queue.RemoveAt(0);
                if (request.Finished) continue;

                // 闲置优先：归还的实例先交付给最早的等待者，既不重复创建也不超出配额。
                if (bucket.TryTakeIdle(out var idle) && idle != null) return PumpResult.Reuse(request, idle.Instance);

                if (bucket.Creating > 0)
                {
                    // 每桶同时最多一次创建：放回队首，等这次创建结束再推进。
                    bucket.Queue.Insert(0, request);
                    return null;
                }
                var quota = bucket.Policy.MaxActivePerResource;
                if (quota is int max && bucket.ActiveCount + bucket.Creating >= max)
                {
                    bucket.Queue.Insert(0, request);
                    return null;
                }
                bucket.Creating++;
                return PumpResult.Create(request);
            }
            return null;
        }

        private async Task _CreateAsync(PoolBucket bucket, RentRequest request)
        {
            request.MarkStarted();
            try
            {
                // 原型与 OnRent/OnReturn 都是引擎可见对象：创建前在串行域内取值，创建后才回到串行域重置。
                var prototype = Context.Invoke(() => bucket.PrototypeValue);
                var instance = await bucket.Factory
                    .CreateAsync(prototype, request.CreationRequest, request.RequestToken)
                    .ConfigureAwait(false);
                if (instance == null)
                {
                    _FailCreate(bucket, request, new LoadError(
                        DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate, CleanupStatus.Complete, null,
                        new Dictionary<string, object> { { "pool", bucket.Key.ToString() } }));
                    return;
                }
                await Context.RunAsync(() => bucket.Factory.OnRent(instance)).ConfigureAwait(false);
                _CompleteCreate(bucket, request, instance);
            }
            catch (OperationCanceledException) when (request.RequestToken.IsCancellationRequested)
            {
                _FailCreate(bucket, request, new LoadError(
                    DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate, CleanupStatus.Unknown,
                    null, new Dictionary<string, object> { { "pool", bucket.Key.ToString() } }));
            }
            catch (Exception error)
            {
                var failure = error is ResourceLoadException load ? load.Error : new LoadError(
                    DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate, CleanupStatus.Unknown, error,
                    new Dictionary<string, object> { { "pool", bucket.Key.ToString() } });
                _FailCreate(bucket, request, failure);
            }
        }

        /// <summary> 创建完成：登记台账并交给等待者；取消后迟到的实例按合法销毁流程处理 </summary>
        private void _CompleteCreate(PoolBucket bucket, RentRequest request, object instance)
        {
            var destroy = false;
            lock (_gate)
            {
                bucket.Creating--;
                request.MarkFinished();
                if (request.RequestToken.IsCancellationRequested || bucket.Closed)
                {
                    destroy = true;
                }
                else
                {
                    request.Succeed(instance, bucket.Track(instance, InstanceLifecycle.Active));
                }
            }
            if (destroy) _ = _DestroyAsync(bucket, instance);
            else _Advance(bucket);
        }
        private void _FailCreate(PoolBucket bucket, RentRequest request, LoadError error)
        {
            lock (_gate)
            {
                bucket.Creating--;
                request.MarkFinished();
            }
            request.Fail(error);
            _Advance(bucket);
        }

        private void _Advance(PoolBucket bucket)
        {
            PumpResult? next;
            lock (_gate) next = _Pump(bucket);
            _RunPumpResult(bucket, next);
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
                else if (bucket.Policy.MaxIdlePerResource <= 0
                         || bucket.IdleCount >= bucket.Policy.MaxIdlePerResource)
                {
                    // 超上限的归还实例不再闲置：直接按台账身份销毁。
                    tracked.Lifecycle = InstanceLifecycle.Gone;
                    destroy = true;
                }
                else
                {
                    bucket.ReleaseIdle(tracked, Now());
                    returnReset = true;
                }
                if (bucket.ActiveCount == 0) bucket.MarkEmpty(Now());
            }

            if (returnReset) bucket.Factory.OnReturn(instance);

            if (destroy)
            {
                _ = _DestroyAsync(bucket, instance);
            }
            else
            {
                _Advance(bucket);
            }
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

        /// <summary> 取一个闲置实例销毁：闲置期满或超出闲置上限时使用 </summary>
        private async Task _TakeIdleAndDestroyAsync(PoolBucket bucket)
        {
            TrackedInstance? victim = null;
            lock (_gate) bucket.TryTakeIdle(out victim);
            if (victim != null) await _DestroyAsync(bucket, victim.Instance).ConfigureAwait(false);
            _Advance(bucket);
        }

        /// <summary> 销毁并确认结束；销毁失败只影响该实例，不由后续查询补救 </summary>
        private async Task _DestroyAsync(PoolBucket bucket, object instance)
        {
            lock (_gate)
            {
                bucket.BeginDestroy(instance);
            }
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
            finally
            {
                lock (_gate) bucket.EndDestroy(instance);
            }
            if (failure != null) Diagnostics.Report(failure);
            _TryClose(bucket);
        }

        /// <summary> 引擎帧或空闲时调用：按闲置寿命修剪实例，并关闭到期的空桶 </summary>
        internal void Tick()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Context.Invoke(() => _Maintain(Now()));
        }

        private void _Maintain(double now)
        {
            foreach (var bucket in new List<PoolBucket>(_buckets.Values))
            {
                if (bucket.Closed) continue;
                var lifetime = bucket.Policy.IdleLifetime;
                if (lifetime <= TimeSpan.Zero)
                {
                    _TryClose(bucket);
                    continue;
                }
                List<object> victims;
                lock (_gate) victims = bucket.TakeExpiredIdle(now, lifetime.TotalSeconds);
                foreach (var victim in victims) _ = _DestroyAsync(bucket, victim);
                _TryClose(bucket);
            }
        }

        /// <summary>
        /// 桶已空、闲置超期时关闭并归还原型引用。
        /// 原型不得提前归还：有闲置、在用实例，或创建/销毁仍在进行时都不关闭。
        /// </summary>
        private void _TryClose(PoolBucket bucket)
        {
            var close = false;
            lock (_gate)
            {
                if (bucket.Closed || !bucket.Opened) return;
                if (bucket.Queue.Count > 0 || bucket.Creating > 0 || bucket.Destroying > 0) return;
                if (bucket.ActiveCount > 0 || bucket.IdleCount > 0) return;
                var lifetime = bucket.Policy.IdleLifetime;
                if (lifetime > TimeSpan.Zero && Now() - bucket.IdleEmptySince < lifetime.TotalSeconds) return;
                bucket.Closed = true;
                _buckets.Remove(bucket.Key);
                close = true;
            }
            if (close) bucket.ReleasePrototype();
        }

        /// <summary> 空桶到期后自行关闭；不依赖业务一定调用 Tick </summary>
        private void _ScheduleClose(PoolBucket bucket)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (bucket.Closed || !bucket.Opened) return;
            if (bucket.IdleCount > 0 || bucket.ActiveCount > 0) return;
            lock (_gate)
            {
                if (bucket.CloseScheduled) return;
                bucket.CloseScheduled = true;
            }
            if (bucket.Policy.IdleLifetime <= TimeSpan.Zero)
            {
                _TryClose(bucket);
                return;
            }
            _ = _CloseAfterIdleAsync(bucket);
        }

        private async Task _CloseAfterIdleAsync(PoolBucket bucket)
        {
            try
            {
                var wait = bucket.Policy.IdleLifetime;
                var elapsed = Now() - bucket.IdleEmptySince;
                if (elapsed < wait.TotalSeconds)
                {
                    await Task.Delay(TimeSpan.FromSeconds(wait.TotalSeconds - elapsed), _shutdown.Token)
                        .ConfigureAwait(false);
                }
                Context.Invoke(() =>
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    _Maintain(Now());
                });
            }
            catch (OperationCanceledException)
            {
                // 退出时不再关闭桶：原型引用由 DisposeAsync 统一归还。
            }
        }

        internal Task ClearPoolAsync(string factoryName, string address, ResourceSource source, CancellationToken ct)
        {
            _ThrowIfDisposed();
            return _ClearPoolAsync(factoryName, address, source, ct);
        }

        /// <summary> 关闭该地址对应的池桶并销毁其闲置实例；在用实例仍按各自租约归还 </summary>
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
                foreach (var bucket in targets) _buckets.Remove(bucket.Key);
            }

            foreach (var bucket in targets)
            {
                List<object> idle;
                lock (_gate) idle = bucket.DrainIdle();
                foreach (var instance in idle) await _DestroyAsync(bucket, instance).ConfigureAwait(false);
                bucket.ReleasePrototype();
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
                        bucket.ActiveCount, bucket.IdleCount,
                        bucket.Creating + bucket.Destroying + bucket.Queue.Count));
                }
            }
            return rows;
        }

        internal async Task DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _shutdown.Cancel();
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
                foreach (var request in bucket.Queue)
                {
                    request.Fail(new LoadError(DiagnosticCodes.LifecycleManagerDisposed,
                        LoadStage.Instantiate, CleanupStatus.Complete));
                }
                bucket.Queue.Clear();
                List<object> idle;
                lock (_gate) idle = bucket.DrainIdle();
                foreach (var instance in idle) destroy.Add(_DestroyAsync(bucket, instance));
                bucket.ReleasePrototype();
            }
            foreach (var task in destroy) await task.ConfigureAwait(false);
            _shutdown.Dispose();
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

            /// <summary> 已经不存在：引擎销毁或已确认销毁完成 </summary>
            Gone,
        }

        internal sealed class TrackedInstance
        {
            internal TrackedInstance(object instance)
            {
                Instance = instance;
            }

            internal object Instance { get; }

            internal InstanceLifecycle Lifecycle { get; set; } = InstanceLifecycle.Active;

            internal double ReturnedAt { get; set; }
        }

        /// <summary>
        /// 一个池桶：原型引用、实例台账与等待者 FIFO。
        /// 台账是实例身份的唯一事实：连续两次归还同一个实例只生效一次。
        /// </summary>
        internal sealed class PoolBucket
        {
            private readonly TaskCompletionSource<bool> _opened =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Dictionary<long, TrackedInstance> _instances = new Dictionary<long, TrackedInstance>();
            private ResourceRef<object>? _prototype;
            private double _idleEmptySince;
            private long _instanceSeed;

            internal PoolBucket(PoolKey key, IInstanceFactory factory, PoolPolicy policy,
                InstancePool owner, double now)
            {
                Key = key;
                Factory = factory;
                Policy = policy;
                Owner = owner;
                _idleEmptySince = now;
            }

            internal PoolKey Key { get; }

            internal IInstanceFactory Factory { get; }

            internal PoolPolicy Policy { get; }

            internal InstancePool Owner { get; }

            internal List<RentRequest> Queue { get; } = new List<RentRequest>();

            /// <summary> 创建中的实例数；与台账分开，因为创建完成前还没有实例身份 </summary>
            internal int Creating { get; set; }

            internal int Destroying { get; set; }

            internal bool Opened { get; set; }

            internal bool Opening { get; set; }

            internal bool Closed { get; set; }

            internal bool CloseScheduled { get; set; }

            internal double IdleEmptySince => _idleEmptySince;

            internal Task OpenedTask => _opened.Task;

            internal object PrototypeValue => _prototype!.Value;

            /// <summary> 在用实例数：从台账投影，不另存计数 </summary>
            internal int ActiveCount
            {
                get
                {
                    var count = 0;
                    foreach (var tracked in _instances.Values)
                    {
                        if (tracked.Lifecycle == InstanceLifecycle.Active) count++;
                    }
                    return count;
                }
            }

            internal int IdleCount
            {
                get
                {
                    var count = 0;
                    foreach (var tracked in _instances.Values)
                    {
                        if (tracked.Lifecycle == InstanceLifecycle.Idle) count++;
                    }
                    return count;
                }
            }

            internal void MarkOpened() => _opened.TrySetResult(true);

            internal void AttachPrototype(ResourceRef<object> reference) => _prototype = reference;

            internal void MarkEmpty(double now) => _idleEmptySince = now;

            /// <summary> 创建完成的实例进入台账，并把它交给调用者 </summary>
            internal long Track(object instance, InstanceLifecycle lifecycle)
            {
                var id = ++_instanceSeed;
                _instances.Add(id, new TrackedInstance(instance) { Lifecycle = lifecycle });
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
                tracked.Lifecycle = InstanceLifecycle.Gone;
                return true;
            }

            internal List<object> TakeExpiredIdle(double now, double lifetimeSeconds)
            {
                List<object>? victims = null;
                foreach (var tracked in _instances.Values)
                {
                    if (tracked.Lifecycle != InstanceLifecycle.Idle) continue;
                    if (now - tracked.ReturnedAt < lifetimeSeconds) continue;
                    tracked.Lifecycle = InstanceLifecycle.Gone;
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
                    tracked.Lifecycle = InstanceLifecycle.Gone;
                    drained.Add(tracked.Instance);
                }
                return drained;
            }

            internal void BeginDestroy(object instance)
            {
                Destroying++;
                foreach (var tracked in _instances.Values)
                {
                    if (!ReferenceEquals(tracked.Instance, instance)) continue;
                    tracked.Lifecycle = InstanceLifecycle.Gone;
                }
            }

            /// <summary> 销毁结束：台账中该实例退出，计数与台账保持一致 </summary>
            internal void EndDestroy(object instance)
            {
                Destroying--;
                List<long>? removed = null;
                foreach (var pair in _instances)
                {
                    if (!ReferenceEquals(pair.Value.Instance, instance)) continue;
                    (removed ??= new List<long>()).Add(pair.Key);
                }
                if (removed == null) return;
                foreach (var id in removed) _instances.Remove(id);
            }

            /// <summary> 原型引用由桶持有到关闭：闲置不释放，桶关闭时才归还 </summary>
            internal void ReleasePrototype()
            {
                var prototype = _prototype;
                _prototype = null;
                prototype?.Dispose();
            }
        }

        /// <summary> 一次桶推进的结果：复用某个闲置实例，或开始一次实际创建 </summary>
        private readonly struct PumpResult
        {
            private PumpResult(RentRequest request, object? idle)
            {
                Request = request;
                Idle = idle;
            }

            internal RentRequest Request { get; }

            internal object? Idle { get; }

            internal static PumpResult Create(RentRequest request) => new PumpResult(request, null);

            internal static PumpResult Reuse(RentRequest request, object idle) => new PumpResult(request, idle);
        }

        internal abstract class RentRequest
        {            protected RentRequest(PoolBucket bucket, ResourceRequest creationRequest, CancellationToken requestToken)
            {
                Bucket = bucket;
                CreationRequest = creationRequest;
                RequestToken = requestToken;
            }

            internal PoolBucket Bucket { get; }

            internal ResourceRequest CreationRequest { get; }

            internal CancellationToken RequestToken { get; }

            internal bool Started { get; private set; }

            internal bool Finished { get; private set; }

            internal void MarkStarted() => Started = true;

            internal void MarkFinished() => Finished = true;

            internal abstract void Succeed(object instance, long instanceId);

            internal abstract void Fail(LoadError error);
        }

        private sealed class RentRequest<T> : RentRequest where T : class
        {
            private readonly CancellationTokenRegistration _cancellation;

            internal RentRequest(PoolBucket bucket, ResourceRequest creationRequest, CancellationToken requestToken)
                : base(bucket, creationRequest, requestToken)
            {
                Waiter = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                // 排队中取消：只结束个人等待，不取消已经开始的实际创建。
                _cancellation = requestToken.Register(() =>
                {
                    if (Finished || Started) return;
                    MarkFinished();
                    Waiter.TrySetCanceled(requestToken);
                });
            }

            internal TaskCompletionSource<T> Waiter { get; }

            internal long InstanceId { get; private set; }

            internal override void Succeed(object instance, long instanceId)
            {
                InstanceId = instanceId;
                if (instance is T typed)
                {
                    Waiter.TrySetResult(typed);
                    return;
                }
                Waiter.TrySetException(new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InstanceUnsupported, LoadStage.Instantiate, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "actualType", instance.GetType().FullName! },
                        { "requestedType", typeof(T).FullName! },
                        { "origin", "pool.succeed" },
                    })));
            }

            internal override void Fail(LoadError error) =>
                Waiter.TrySetException(new ResourceLoadException(error));

            internal void DisposeRegistration() => _cancellation.Dispose();
        }
    }
}
