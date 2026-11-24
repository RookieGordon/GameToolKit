/*
 * description  : 实例池。按原型身份分桶；桶通过独立 ResourceRef 聚合持有一份原型引用，不依赖缓存内部实现；
 *                有闲置就复用、无闲置就创建：没有活跃上限、没有池内排队 (需求 Q04)。
 *                清池 = 关闭桶：取走并销毁闲置实例；已接纳的租用照常交付、归还时直接销毁；
 *                在用、创建中、销毁失败的实例都还需要原型，全部结束后桶才归还原型引用。
 *                准入点 = 取到闲置实例或开始本次创建；仍在等原型的租用不算已接纳，桶关闭后以 pool_closed 结束。
 *                一把锁串行桶状态；工厂调用 (创建/准备/重置/销毁) 都在执行上下文内。
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 销毁失败后保留的实例残留：对象与原型依赖仍由原桶负责，下次清池再试 </summary>
    public sealed class InstanceResidue
    {
        internal InstanceResidue(ResourceKey prototypeKey, object instance, Exception error)
        {
            PrototypeKey = prototypeKey;
            Instance = instance;
            Error = error;
        }

        public ResourceKey PrototypeKey { get; }
        public object Instance { get; }
        public Exception Error { get; }
    }

    /// <summary> 一次清池的结果 </summary>
    public sealed class PoolClearResult
    {
        public int Destroyed { get; internal set; }
        /// <summary> 仍在业务手里的租约数：归还时直接销毁 </summary>
        public int StillActive { get; internal set; }
        public List<InstanceResidue> Failed { get; } = new List<InstanceResidue>();
    }

    /// <summary> 只读排查：一个桶的状态 </summary>
    public readonly struct PoolBucketInfo
    {
        public readonly ResourceKey PrototypeKey;
        public readonly int Idle;
        public readonly int Active;
        public readonly int Creating;
        public readonly bool Closed;

        internal PoolBucketInfo(ResourceKey prototypeKey, int idle, int active, int creating, bool closed)
        {
            PrototypeKey = prototypeKey;
            Idle = idle;
            Active = active;
            Creating = creating;
            Closed = closed;
        }
    }

    public sealed class InstancePool
    {
        private readonly object _gate = new object();
        private readonly Dictionary<ResourceKey, Bucket> _open = new Dictionary<ResourceKey, Bucket>();
        private readonly HashSet<Bucket> _closing = new HashSet<Bucket>();
        private readonly List<InstanceResidue> _residues = new List<InstanceResidue>();
        private readonly IInstanceFactory _factory;
        private readonly PoolPolicy _policy;
        private readonly Func<ResourceKey, CancellationToken, Task<ResourceRef<object>>> _acquirePrototype;
        private readonly IExecutionContext _context;

        /// <param name="acquirePrototype">按原型身份取得一份独立引用 (走资源主流程；共享与超时由它负责)</param>
        public InstancePool(
            IInstanceFactory factory,
            PoolPolicy policy,
            Func<ResourceKey, CancellationToken, Task<ResourceRef<object>>> acquirePrototype,
            IExecutionContext context)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _policy = policy;
            _acquirePrototype = acquirePrototype ?? throw new ArgumentNullException(nameof(acquirePrototype));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Type PrototypeType => _factory.PrototypeType;

        /// <summary> 租一个实例：原型到位 → 准入 → 复用或创建 → 交付租约。token 只结束本人。 </summary>
        public async Task<InstanceLease<T>> RentAsync<T>(ResourceKey prototypeKey, CancellationToken token)
        {
            var bucket = BucketFor(prototypeKey);
            var prototype = await _acquirePrototype(prototypeKey, token).ConfigureAwait(false);

            object? instance = null;
            object? prototypeValue = null;
            var releaseRedundant = false;
            var closed = false;
            lock (_gate)
            {
                if (bucket.Closed)
                {
                    closed = true;
                }
                else
                {
                    if (bucket.Prototype == null) bucket.Prototype = prototype;
                    else releaseRedundant = true;

                    if (bucket.Idle.Count > 0)
                    {
                        instance = bucket.Idle.Dequeue();
                        bucket.Active++;
                    }
                    else
                    {
                        bucket.Creating++;
                        prototypeValue = bucket.Prototype.Value;
                    }
                }
            }

            if (closed)
            {
                prototype.Dispose();
                throw ResourceException.Of(DiagnosticCodes.InstancePoolClosed, LoadStage.Instantiate, prototypeKey);
            }
            if (releaseRedundant) prototype.Dispose();

            instance = instance == null
                ? await CreateInstanceAsync(bucket, prototypeKey, prototypeValue!).ConfigureAwait(false)
                : await PrepareReusedAsync(bucket, prototypeKey, instance).ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                await _context.RunAsync(() => ReturnInContext(bucket, instance, true)).ConfigureAwait(false);
                throw new OperationCanceledException(token);
            }
            return new InstanceLease<T>(this, bucket, instance);
        }

        /// <summary> 关闭选中的桶：销毁闲置，已有租约归还时销毁，全部对象结束后归还原型。可重复调用。 </summary>
        public async Task<PoolClearResult> ClearAsync(Func<ResourceKey, bool>? selector = null)
        {
            var result = new PoolClearResult();
            var work = new List<KeyValuePair<Bucket, List<object>>>();
            lock (_gate)
            {
                foreach (var bucket in new List<Bucket>(_open.Values))
                {
                    if (selector != null && !selector(bucket.Key)) continue;
                    bucket.Closed = true;
                    _open.Remove(bucket.Key);
                    _closing.Add(bucket);
                }
                foreach (var bucket in _closing)
                {
                    if (selector != null && !selector(bucket.Key)) continue;
                    var idle = new List<object>(bucket.Idle);
                    bucket.Idle.Clear();
                    for (var i = _residues.Count - 1; i >= 0; i--)
                    {
                        if (!_residues[i].PrototypeKey.Equals(bucket.Key)) continue;
                        idle.Add(_residues[i].Instance);
                        _residues.RemoveAt(i);
                        bucket.Residues--;
                    }
                    result.StillActive += bucket.Active + bucket.Creating;
                    work.Add(new KeyValuePair<Bucket, List<object>>(bucket, idle));
                }
            }

            foreach (var item in work)
            {
                foreach (var instance in item.Value)
                {
                    var residue = await _context.RunAsync(() => Destroy(item.Key, instance)).ConfigureAwait(false);
                    if (residue == null) result.Destroyed++;
                    else result.Failed.Add(residue);
                }
                TryRetire(item.Key);
            }
            return result;
        }

        public void Snapshot(List<PoolBucketInfo>? buckets, List<InstanceResidue>? residues)
        {
            lock (_gate)
            {
                if (buckets != null)
                {
                    foreach (var bucket in _open.Values) buckets.Add(bucket.Info());
                    foreach (var bucket in _closing) buckets.Add(bucket.Info());
                }
                residues?.AddRange(_residues);
            }
        }

        /// <summary> 租约归还 (任意线程)：投递到上下文内做重置/销毁，不阻塞业务线程 </summary>
        internal void Return(Bucket bucket, object instance)
        {
            _context.Post(() => ReturnInContext(bucket, instance, false));
        }

        private async Task<object> CreateInstanceAsync(Bucket bucket, ResourceKey prototypeKey, object prototypeValue)
        {
            object instance;
            try
            {
                instance = await _context.RunAsync(() =>
                {
                    var created = _factory.Create(prototypeValue) ?? throw new InvalidOperationException("工厂返回了 null");
                    try { _factory.OnRent(created); }
                    catch { Destroy(bucket, created); throw; }
                    return created;
                }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lock (_gate) bucket.Creating--;
                TryRetire(bucket);
                throw ResourceException.Of(DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate, prototypeKey, error);
            }

            lock (_gate)
            {
                bucket.Creating--;
                bucket.Active++;
            }
            return instance;
        }

        private async Task<object> PrepareReusedAsync(Bucket bucket, ResourceKey prototypeKey, object instance)
        {
            try
            {
                await _context.RunAsync(() => _factory.OnRent(instance)).ConfigureAwait(false);
                return instance;
            }
            catch (Exception error)
            {
                await _context.RunAsync(() => ReturnInContext(bucket, instance, true)).ConfigureAwait(false);
                throw ResourceException.Of(DiagnosticCodes.InstanceResetFailed, LoadStage.Instantiate, prototypeKey, error);
            }
        }

        private void ReturnInContext(Bucket bucket, object instance, bool forceDestroy)
        {
            var keep = !forceDestroy;
            if (keep)
            {
                try { _factory.OnReturn(instance); }
                catch (Exception error) { keep = false; Log.Error("[InstancePool] 实例重置失败，改为销毁: " + error); }
            }
            lock (_gate)
            {
                bucket.Active--;
                if (keep && !bucket.Closed && bucket.Idle.Count < _policy.MaxIdlePerPrototype)
                {
                    bucket.Idle.Enqueue(instance);
                    return;
                }
            }
            Destroy(bucket, instance);
            TryRetire(bucket);
        }

        private InstanceResidue? Destroy(Bucket bucket, object instance)
        {
            try
            {
                _factory.Destroy(instance);
                return null;
            }
            catch (Exception error)
            {
                var residue = new InstanceResidue(bucket.Key, instance, error);
                lock (_gate)
                {
                    _residues.Add(residue);
                    bucket.Residues++;
                }
                return residue;
            }
        }

        private void TryRetire(Bucket bucket)
        {
            ResourceRef<object>? prototype;
            lock (_gate)
            {
                if (!bucket.Closed || bucket.Active > 0 || bucket.Creating > 0 || bucket.Idle.Count > 0 || bucket.Residues > 0)
                    return;
                if (!_closing.Remove(bucket)) return;
                prototype = bucket.Prototype;
                bucket.Prototype = null;
            }
            prototype?.Dispose();
        }

        private Bucket BucketFor(ResourceKey key)
        {
            lock (_gate)
            {
                if (!_open.TryGetValue(key, out var bucket))
                {
                    bucket = new Bucket(key);
                    _open[key] = bucket;
                }
                return bucket;
            }
        }

        internal sealed class Bucket
        {
            public readonly ResourceKey Key;
            public readonly Queue<object> Idle = new Queue<object>();
            public ResourceRef<object>? Prototype;
            public int Active;
            public int Creating;
            public int Residues;
            public bool Closed;

            public Bucket(ResourceKey key) => Key = key;

            public PoolBucketInfo Info() => new PoolBucketInfo(Key, Idle.Count, Active, Creating, Closed);
        }
    }

    /// <summary> 实例租约：一个业务对一个实例的持有，归还即失效；归还后实例回池或销毁由池决定 </summary>
    public sealed class InstanceLease<T> : IDisposable
    {
        private readonly InstancePool _pool;
        private readonly InstancePool.Bucket _bucket;
        private readonly object _instance;
        private int _disposed;

        internal InstanceLease(InstancePool pool, InstancePool.Bucket bucket, object instance)
        {
            _pool = pool;
            _bucket = bucket;
            _instance = instance;
        }

        public T Value
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw ResourceException.Of(DiagnosticCodes.AssetInvalidated, LoadStage.Instantiate, _bucket.Key);
                return (T)_instance;
            }
        }

        public ResourceKey PrototypeKey => _bucket.Key;
        public bool IsValid => Volatile.Read(ref _disposed) == 0;

        /// <summary> 归还；幂等，任意线程，不阻塞 </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _pool.Return(_bucket, _instance);
        }
    }
}
