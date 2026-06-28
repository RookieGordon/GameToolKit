/*
 * author       : Gordon
 * datetime     : 2026/6/27
 * description  : 共享资源来源 (引擎无关, 程序集内部)。本质是"按 key 管理共享 AssetHandle 的缓存":
 *                  - 加载器注册与路由 (ELoadType / 地址协议);
 *                  - KeyedAsyncLock 保证同一 address 只加载一次;
 *                  - address -> AssetHandle 缓存 (每个 key 对应唯一的共享句柄, 非可互换实例, 故是缓存而非对象池);
 *                  - 引用归零 -> 立即或延迟卸载, 命中可复活; CollectUnused 推进延迟卸载。
 *                作为 IBackingSource 产出资源型背书 (AssetBacking)。本层不认识实例、ResourceRef、token。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
 
namespace ToolKit.Tools.Common
{
    internal sealed class SharedAssetSource : IBackingSource
    {
        private readonly Dictionary<string, AssetHandle> _cache = new Dictionary<string, AssetHandle>();
        private readonly Dictionary<ELoadType, ILoader> _loadersByType = new Dictionary<ELoadType, ILoader>();
        private readonly List<ILoader> _loaders = new List<ILoader>();
        private readonly KeyedAsyncLock _loadLock = new KeyedAsyncLock();
        private readonly object _cacheGate = new object();

        private readonly Dictionary<string, DateTime> _pendingUnload = new Dictionary<string, DateTime>();
        private readonly TimeSpan _unloadDelay;

        public SharedAssetSource(double unloadDelaySeconds = 0)
        {
            _unloadDelay = TimeSpan.FromSeconds(Math.Max(0, unloadDelaySeconds));
        }

        public int CachedCount
        {
            get { lock (_cacheGate) return _cache.Count; }
        }

        public void RegisterLoader(ILoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }
            _loadersByType[loader.LoadType] = loader;
            if (!_loaders.Contains(loader))
            {
                _loaders.Add(loader);
            }
        }

        public bool TryGetCached(string address, out AssetHandle handle)
        {
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(address, out var h) && h.Status != ELoadStatus.Unloaded)
                {
                    handle = h;
                    return true;
                }
            }
            handle = null;
            return false;
        }

        // —— IBackingSource: 产出资源型背书 ——
        public async Task<IRefBacking> AcquireAsync(string key, ELoadType loadType = ELoadType.Auto, CancellationToken cancellationToken = default)
        {
            var handle = await LoadHandleAsync(key, loadType, cancellationToken).ConfigureAwait(false);
            if (handle == null || !handle.IsSuccess)
            {
                return null;
            }
            return new AssetBacking(handle);
        }

        /// <summary> 加载并返回引用已 +1 的句柄。同一 address 并发只加载一次, 其余复用。 </summary>
        public async Task<AssetHandle> LoadHandleAsync(string address, ELoadType loadType = ELoadType.Auto, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("address 不能为空", nameof(address));
            }

            using (await _loadLock.LockAsync(address, cancellationToken).ConfigureAwait(false))
            {
                // 命中缓存 -> 复用 + 复活
                lock (_cacheGate)
                {
                    if (_cache.TryGetValue(address, out var cached) && cached.Status != ELoadStatus.Unloaded)
                    {
                        cached.Retain();
                        _pendingUnload.Remove(address);
                        return cached;
                    }
                }

                var loader = _ResolveLoader(address, loadType);
                if (loader == null)
                {
                    throw new InvalidOperationException(
                        $"[ResourceSystem] 找不到可处理的加载器: address={address}, loadType={loadType}");
                }

                var rawHandle = await loader.LoadAsync(address, cancellationToken).ConfigureAwait(false);
                if (rawHandle is not AssetHandle handle)
                {
                    Log.Error($"[ResourceSystem] 加载器返回的句柄非 AssetHandle, 无法纳入缓存管理: {address}");
                    return rawHandle as AssetHandle;
                }

                if (!handle.IsSuccess)
                {
                    return handle; // 失败不缓存, 引用计数为 0
                }

                handle.OnReachedZero = _OnHandleReachedZero;
                lock (_cacheGate)
                {
                    _cache[address] = handle;
                    _pendingUnload.Remove(address);
                }
                handle.Retain();
                return handle;
            }
        }

        private ILoader _ResolveLoader(string address, ELoadType loadType)
        {
            if (loadType != ELoadType.Auto)
            {
                return _loadersByType.TryGetValue(loadType, out var l) ? l : null;
            }
            for (int i = 0; i < _loaders.Count; i++)
            {
                if (_loaders[i].CanLoad(address))
                {
                    return _loaders[i];
                }
            }
            return null;
        }

        // 引用归零: 立即卸载 (delay<=0) 或登记待卸载 (delay>0, 命中可复活)
        private void _OnHandleReachedZero(AssetHandle handle)
        {
            lock (_cacheGate)
            {
                if (_unloadDelay <= TimeSpan.Zero)
                {
                    _UnloadAndRemove_NoLock(handle);
                }
                else
                {
                    _pendingUnload[handle.Address] = DateTime.UtcNow;
                }
            }
        }

        // 调用方需持有 _cacheGate
        private void _UnloadAndRemove_NoLock(AssetHandle handle)
        {
            if (_cache.TryGetValue(handle.Address, out var cached) && ReferenceEquals(cached, handle))
            {
                _cache.Remove(handle.Address);
            }
            _pendingUnload.Remove(handle.Address);
            handle.Unload();
            Log.Debug($"[ResourceSystem] 资源已卸载并移出缓存: {handle.Address}");
        }

        /// <summary> 推进延迟卸载: 卸载引用已归零且超过延迟时间的资源。需外部周期性调用。 </summary>
        public void CollectUnused()
        {
            lock (_cacheGate)
            {
                if (_pendingUnload.Count == 0)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                List<AssetHandle> toUnload = null;
                List<string> stale = null;

                foreach (var kv in _pendingUnload)
                {
                    if (!_cache.TryGetValue(kv.Key, out var h))
                    {
                        (stale ??= new List<string>()).Add(kv.Key);
                    }
                    else if (h.ReferenceCount > 0)
                    {
                        (stale ??= new List<string>()).Add(kv.Key); // 已复活
                    }
                    else if (now - kv.Value >= _unloadDelay)
                    {
                        (toUnload ??= new List<AssetHandle>()).Add(h);
                    }
                }

                if (stale != null)
                {
                    foreach (var k in stale) _pendingUnload.Remove(k);
                }
                if (toUnload != null)
                {
                    foreach (var h in toUnload) _UnloadAndRemove_NoLock(h);
                }
            }
        }

        /// <summary> 卸载全部并清空 (用于关闭) </summary>
        public void Clear()
        {
            lock (_cacheGate)
            {
                foreach (var h in _cache.Values)
                {
                    h.Unload();
                }
                _cache.Clear();
                _pendingUnload.Clear();
            }
            _loaders.Clear();
            _loadersByType.Clear();
        }
    }
}