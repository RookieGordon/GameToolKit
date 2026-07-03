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
        private readonly Dictionary<ILoader, SemaphoreSlim> _loadLimiters = new Dictionary<ILoader, SemaphoreSlim>();
        private readonly List<ILoader> _loaders = new List<ILoader>();
        private readonly KeyedAsyncLock _loadLock = new KeyedAsyncLock();
        private readonly object _cacheGate = new object();
        private readonly object _loaderGate = new object();

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
            _EnsureLoadLimiter(loader);
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

		// —— IBackingSource: 产出资源型背书 (失败携带 LoadError) ——
        public async Task<AcquireResult> AcquireAsync(string key, ELoadType loadType = ELoadType.Auto, CancellationToken cancellationToken = default)
        {
            var handle = await LoadHandleAsync(key, loadType, cancellationToken).ConfigureAwait(false);
            return _ToAcquireResult(key, handle);
        }

        public async Task<AssetHandle> LoadHandleAsync(string address, ELoadType loadType = ELoadType.Auto, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ResourceException(ELoadError.InvalidAddress, "address cannot be empty");
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
                    throw new ResourceException(ELoadError.NoLoader,
                        $"No loader can handle address={address}, loadType={loadType}");
                }

                var rawHandle = await _LoadWithLimiterAsync(loader, address, cancellationToken).ConfigureAwait(false);
                if (rawHandle is not AssetHandle handle)
                {
                    Log.Error($"[ResourceSystem] Loader returned non-AssetHandle for address: {address}");
                    return rawHandle as AssetHandle;
                }

                if (!handle.IsSuccess)
                {
                    return handle;
                }

                handle.OnReachedZero = h => _OnHandleReachedZero(address, h);
                lock (_cacheGate)
                {
                    _cache[address] = handle;
                    _pendingUnload.Remove(address);
                }
                handle.Retain();
                return handle;
            }
        }

        public void CollectUnused()
        {
            lock (_cacheGate)
            {
                if (_pendingUnload.Count == 0)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                List<string> toUnload = null;
                List<string> stale = null;

                foreach (var kv in _pendingUnload)
                {
                    if (!_cache.TryGetValue(kv.Key, out var h))
                    {
                        (stale ??= new List<string>()).Add(kv.Key);
                    }
                    else if (h.ReferenceCount > 0)
                    {
                        (stale ??= new List<string>()).Add(kv.Key);
                    }
                    else if (now - kv.Value >= _unloadDelay)
                    {
                        (toUnload ??= new List<string>()).Add(kv.Key);
                    }
                }

                if (stale != null)
                {
                    foreach (var key in stale)
                    {
                        _pendingUnload.Remove(key);
                    }
                }

                if (toUnload != null)
                {
                    foreach (var key in toUnload)
                    {
                        if (_cache.TryGetValue(key, out var h))
                        {
                            _UnloadAndRemove_NoLock(key, h);
                        }
                    }
                }
            }
        }

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
            lock (_loaderGate)
            {
                foreach (var limiter in _loadLimiters.Values)
                {
                    limiter.Dispose();
                }
                _loadLimiters.Clear();
            }
        }

        private static AcquireResult _ToAcquireResult(string key, AssetHandle handle)
        {
            if (handle == null || !handle.IsSuccess)
            {
                return new AcquireResult(handle?.Error ?? new LoadError(ELoadError.Unknown, $"Load failed: {key}"));
            }

            return new AcquireResult(new AssetBacking(handle));
        }

        private ILoader _ResolveLoader(string address, ELoadType loadType)
        {
            if (loadType != ELoadType.Auto)
            {
                return _loadersByType.TryGetValue(loadType, out var loader) ? loader : null;
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

        private SemaphoreSlim _EnsureLoadLimiter(ILoader loader)
        {
            if (loader == null || loader.MaxConcurrentLoads <= 0)
            {
                return null;
            }

            lock (_loaderGate)
            {
                if (!_loadLimiters.TryGetValue(loader, out var limiter))
                {
                    var max = Math.Max(1, loader.MaxConcurrentLoads);
                    limiter = new SemaphoreSlim(max, max);
                    _loadLimiters[loader] = limiter;
                }
                return limiter;
            }
        }

        private async Task<IAssetHandle> _LoadWithLimiterAsync(
            ILoader loader,
            string address,
            CancellationToken cancellationToken)
        {
            var limiter = _EnsureLoadLimiter(loader);
            if (limiter == null)
            {
                return await loader.LoadAsync(address, cancellationToken).ConfigureAwait(false);
            }

            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await loader.LoadAsync(address, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                limiter.Release();
            }
        }

		// 引用归零: 立即卸载 (delay<=0) 或登记待卸载 (delay>0, 命中可复活)
        private void _OnHandleReachedZero(string cacheKey, AssetHandle handle)
        {
            lock (_cacheGate)
            {
                if (_unloadDelay <= TimeSpan.Zero)
                {
                    _UnloadAndRemove_NoLock(cacheKey, handle);
                }
                else
                {
                    _pendingUnload[cacheKey] = DateTime.UtcNow;
                }
            }
        }

		// 调用方需持有 _cacheGate
        private void _UnloadAndRemove_NoLock(string cacheKey, AssetHandle handle)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && ReferenceEquals(cached, handle))
            {
                _cache.Remove(cacheKey);
            }
            _pendingUnload.Remove(cacheKey);
            handle.Unload();
            Log.Debug($"[ResourceSystem] Resource unloaded and removed from cache: {handle.Address}");
        }

    }
}
