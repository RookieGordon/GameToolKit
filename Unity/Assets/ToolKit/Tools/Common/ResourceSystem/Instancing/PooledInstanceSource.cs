/*
 * author       : Gordon
 * datetime     : 2026/6/27
 * description  : 池化实例来源 (引擎无关, 程序集内部)。架在 SharedAssetSource 之上, 按 address 管理"多个对象池"
 *                (本身是池的管理者, 不是单个池)。职责: 取原型 -> 建池 -> 取/还实例 + 在用计数 + 角色护栏。
 *                设计要点:
 *                  - 纯净的 ObjectPool<T> 不持锁; 本来源只持一把廉价 Monitor (_gate), 无异步锁;
 *                  - 原型加载的去重直接复用 SharedAssetSource 的加载锁; 建池竞争用同步 _gate 解决
 *                    (输家归还自己多拿的那次原型引用), 因此不再需要独立的池创建异步锁;
 *                  - 池的取/还 (pool.Get/Return, 含 provider 回调) 一律在 _gate 内, 保证线程安全。
 *                作为 IBackingSource 产出实例型背书 (InstanceBacking)。引擎相关创建/销毁/存活由 IInstanceProvider 完成。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    internal sealed class PooledInstanceSource : IBackingSource
    {
        private readonly SharedAssetSource _assetSource;
        private readonly IInstanceProvider _provider;
        private readonly int _capacity;

        private readonly Dictionary<string, ObjectPool<object>> _pools = new Dictionary<string, ObjectPool<object>>();
        private readonly Dictionary<string, IAssetHandle> _prototypes = new Dictionary<string, IAssetHandle>();
        private readonly Dictionary<string, int> _liveCounts = new Dictionary<string, int>();
        private readonly object _gate = new object();

        public PooledInstanceSource(SharedAssetSource assetSource, IInstanceProvider provider, int capacity = 100)
        {
            _assetSource = assetSource ?? throw new ArgumentNullException(nameof(assetSource));
            _provider = provider;
            _capacity = capacity;
        }

        public bool HasProvider => _provider != null;

        public bool IsAlive(object instance)
        {
            return _provider == null ? instance != null : _provider.IsAlive(instance);
        }

        public int GetLiveCount(string address)
        {
            lock (_gate)
            {
                _liveCounts.TryGetValue(address, out var c);
                return c;
            }
        }

        // —— IBackingSource: 产出实例型背书 (失败携带 LoadError) ——
        public async Task<AcquireResult> AcquireAsync(
            string key,
            ELoadType loadType = ELoadType.Auto,
            CancellationToken cancellationToken = default)
        {
            var (instance, error) = await _AcquireCoreAsync(key, loadType, cancellationToken).ConfigureAwait(false);
            return instance != null
                ? new AcquireResult(new InstanceBacking(this, key, instance))
                : new AcquireResult(error);
        }

        /// <summary> 异步取一个实例 (必要时加载原型并建池)。在用计数 +1。失败返回 null。 </summary>
        public async Task<object> AcquireInstanceAsync(
            string address,
            ELoadType loadType = ELoadType.Auto,
            CancellationToken cancellationToken = default)
        {
            return (await _AcquireCoreAsync(address, loadType, cancellationToken).ConfigureAwait(false)).instance;
        }

        // 核心: 取实例; 失败返回结构化错误 (原型加载失败透传其 LoadError; 角色护栏抛 ResourceException)
        private async Task<(object instance, LoadError error)> _AcquireCoreAsync(
            string address, ELoadType loadType, CancellationToken cancellationToken)
        {
            _EnsureProvider();

            ObjectPool<object> pool;
            lock (_gate)
            {
                _pools.TryGetValue(address, out pool);
            }

            if (pool == null)
            {
                // 原型加载由 SharedAssetSource 自带的加载锁去重; 这里并发拿到的是同一句柄(各 +1 引用)
                var prototype = await _assetSource.LoadHandleAsync(address, loadType, cancellationToken)
                    .ConfigureAwait(false);
                if (prototype == null || !prototype.IsSuccess)
                {
                    return (null, prototype?.Error ?? new LoadError(ELoadError.Unknown, $"原型加载失败: {address}"));
                }
                pool = _GetOrBuildPool(address, prototype); // 不可池化时抛 ResourceException
            }

            return (_GetFromPool(address, pool), LoadError.None);
        }

        /// <summary> 同步取一个实例, 要求原型已在缓存中, 否则返回 null。在用计数 +1。 </summary>
        public object AcquireInstanceCached(string address)
        {
            _EnsureProvider();

            ObjectPool<object> pool;
            lock (_gate)
            {
                _pools.TryGetValue(address, out pool);
            }

            if (pool == null)
            {
                if (!_assetSource.TryGetCached(address, out var prototype))
                {
                    return null;
                }
                prototype.Retain(); // 我先拿一份原型引用
                pool = _GetOrBuildPool(address, prototype);
            }

            return _GetFromPool(address, pool);
        }

        /// <summary> 归还实例: 存活则入池, 已被引擎销毁则丢弃。在用计数 -1。 </summary>
        public void Release(string address, object instance)
        {
            lock (_gate)
            {
                if (_pools.TryGetValue(address, out var pool) && instance != null && IsAlive(instance))
                {
                    pool.Return(instance); // 池操作在 _gate 内, 保证线程安全
                }
                _DecCount_NoLock(address);
            }
        }

        /// <summary> 释放某地址的实例池: 销毁池中实例, 释放原型引用。 </summary>
        public void ReleaseInstancePool(string address)
        {
            ObjectPool<object> pool;
            IAssetHandle prototype;
            lock (_gate)
            {
                _pools.TryGetValue(address, out pool);
                _pools.Remove(address);
                _prototypes.TryGetValue(address, out prototype);
                _prototypes.Remove(address);
                _liveCounts.Remove(address);
                pool?.Clear();
            }
            prototype?.Release();
        }

        public void Clear()
        {
            List<IAssetHandle> protos;
            lock (_gate)
            {
                foreach (var pool in _pools.Values)
                {
                    pool.Clear();
                }
                _pools.Clear();
                protos = new List<IAssetHandle>(_prototypes.Values);
                _prototypes.Clear();
                _liveCounts.Clear();
            }
            foreach (var p in protos)
            {
                p.Release();
            }
        }

        // 取池中对象 + 计数, 全程在 _gate 内 (ObjectPool 非线程安全, 由本锁保护)
        private object _GetFromPool(string address, ObjectPool<object> pool)
        {
            lock (_gate)
            {
                var instance = pool.Get();
                _liveCounts.TryGetValue(address, out var c);
                _liveCounts[address] = c + 1;
                return instance;
            }
        }

        // 已加载原型 -> 建池(或并发竞争中复用已建好的池, 归还多拿的原型引用)。含角色护栏。
        private ObjectPool<object> _GetOrBuildPool(string address, IAssetHandle prototype)
        {
            if (!_provider.CanInstantiate(prototype))
            {
                prototype.Release(); // 交予本方法的那次引用需归还
                throw new ResourceException(ELoadError.NotInstantiable,
                    $"该资源不可被实例化/池化: {address}。可被应用的共享资源应通过 LoadRefAsync / ResourceBinder.ApplyAsync 使用, 而非实例池。");
            }

            lock (_gate)
            {
                if (_pools.TryGetValue(address, out var existed))
                {
                    prototype.Release(); // 建池竞争输家: 归还多拿的那次原型引用
                    return existed;
                }

                var pool = new ObjectPool<object>(
                    factory: () => _provider.Create(prototype),
                    onGet: _provider.OnGet,
                    onReturn: _provider.OnReturn,
                    onDestroy: _provider.OnDestroy,
                    capacity: _capacity);
                _pools[address] = pool;
                _prototypes[address] = prototype; // 池持有这一份原型引用直到 ReleaseInstancePool/Clear
                return pool;
            }
        }

        // 调用方需持有 _gate
        private void _DecCount_NoLock(string address)
        {
            if (_liveCounts.TryGetValue(address, out var c))
            {
                if (c <= 1) _liveCounts.Remove(address);
                else _liveCounts[address] = c - 1;
            }
        }

        private void _EnsureProvider()
        {
            if (_provider == null)
            {
                throw new ResourceException(ELoadError.InstancerMissing,
                    "未注入 IInstanceProvider, 不支持实例化。请先 RegisterInstancer。");
            }
        }
    }
}
