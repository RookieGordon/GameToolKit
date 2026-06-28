/*
 * author       : Gordon
 * datetime     : 2026/6/27
 * description  : 资源管理器门面 (业务唯一入口)。组合两个来源、自身只管"业务凭证":
 *                  - 共享资源来源 SharedAssetSource   : 加载器路由 + 句柄缓存 + 延迟卸载 (internal);
 *                  - 池化实例来源 PooledInstanceSource: 实例池 + 原型持有 + 在用计数 (internal, 依赖前者);
 *                  - 本层                              : ResourceRef 发放 + token 登记 + 重复释放/泄漏检测 + 凭证池。
 *                两个来源都实现 IBackingSource, 因此 LoadRefAsync / InstantiateRefAsync 收束为同一条发放路径,
 *                凭证的取/释放差异由 IRefBacking 多态承担, 本层不含 if(kind) 分支。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public sealed class ResourceManager : IDisposable
    {
        private readonly SharedAssetSource _assetSource;
        private PooledInstanceSource _instanceSource;   // 由 RegisterInstancer 装配, 未注册则为 null

        private long _tokenSeed;
        private readonly Dictionary<long, ResourceRef> _liveRefs = new Dictionary<long, ResourceRef>();
        private readonly ObjectPool<ResourceRef> _refPool;
        private readonly object _refGate = new object();

        private bool _disposed;

        /// <param name="unloadDelaySeconds">引用归零后的延迟卸载时间(秒)。&lt;=0 表示立即卸载</param>
        public ResourceManager(double unloadDelaySeconds = 0)
        {
            _assetSource = new SharedAssetSource(unloadDelaySeconds);
            _refPool = new ObjectPool<ResourceRef>(() => new ResourceRef(), capacity: 200);
        }

        #region 组合装配 (注册式) —— 初始化即显式组合

        /// <summary> 注册一个加载器组件 (加载来源) </summary>
        public void RegisterLoader(ILoader loader) => _assetSource.RegisterLoader(loader);

        /// <summary>
        /// 注册实例器组件 (组合实例化能力)。未注册则不支持实例化 API。
        /// </summary>
        /// <param name="instancer">实例器 (引擎相关的创建/激活/失活/销毁/存活)</param>
        /// <param name="poolCapacity">每个地址实例池的容量上限</param>
        public void RegisterInstancer(IInstanceProvider instancer, int poolCapacity = 100)
        {
            if (instancer == null)
            {
                throw new ArgumentNullException(nameof(instancer));
            }
            if (_instanceSource != null)
            {
                Log.Error("[ResourceSystem] 实例器已注册, 忽略重复注册");
                return;
            }
            _instanceSource = new PooledInstanceSource(_assetSource, instancer, poolCapacity);
        }

        #endregion

        #region 加载 / 缓存 (委托共享资源来源)

        public int CachedCount => _assetSource.CachedCount;

        public bool TryGetCached(string address, out IAssetHandle handle)
        {
            if (_assetSource.TryGetCached(address, out var h))
            {
                handle = h;
                return true;
            }
            handle = null;
            return false;
        }

        public async Task<IAssetHandle> LoadAssetAsync(
            string address,
            ELoadType loadType = ELoadType.Auto,
            CancellationToken cancellationToken = default)
        {
            _CheckDisposed();
            return await _assetSource.LoadHandleAsync(address, loadType, cancellationToken).ConfigureAwait(false);
        }

        public void CollectUnused()
        {
            if (_disposed) return;
            _assetSource.CollectUnused();
        }

        #endregion

        #region 实例化 (委托池化实例来源)

        public T Instantiate<T>(string address) where T : class
        {
            _EnsureInstancer();
            return _instanceSource.AcquireInstanceCached(address) as T;
        }

        public async Task<T> InstantiateAsync<T>(string address, CancellationToken cancellationToken = default)
            where T : class
        {
            _CheckDisposed();
            _EnsureInstancer();
            return await _instanceSource.AcquireInstanceAsync(address, ELoadType.Auto, cancellationToken)
                .ConfigureAwait(false) as T;
        }

        public void Recycle<T>(string address, T instance) where T : class
        {
            _instanceSource?.Release(address, instance);
        }

        public void ReleaseInstancePool(string address) => _instanceSource?.ReleaseInstancePool(address);

        public int GetLiveInstanceCount(string address) => _instanceSource?.GetLiveCount(address) ?? 0;

        private void _EnsureInstancer()
        {
            if (_instanceSource == null)
            {
                throw new ResourceException(ELoadError.InstancerMissing,
                    "未注册实例器, 不支持实例化。请先调用 RegisterInstancer。");
            }
        }

        #endregion

        #region 业务凭证 ResourceRef

        public Task<ResourceRef> LoadRefAsync(
            string address,
            ELoadType loadType = ELoadType.Auto,
            CancellationToken cancellationToken = default)
        {
            _CheckDisposed();
            return _IssueFromAsync(_assetSource, address, loadType, cancellationToken);
        }

        public Task<ResourceRef> InstantiateRefAsync(
            string address, CancellationToken cancellationToken = default)
        {
            _CheckDisposed();
            _EnsureInstancer();
            return _IssueFromAsync(_instanceSource, address, ELoadType.Auto, cancellationToken);
        }

        // 统一发放路径: 任意 IBackingSource 取一份结果 -> 成功发凭证, 失败发"携带 LoadError 的失败凭证"。
        private async Task<ResourceRef> _IssueFromAsync(
            IBackingSource source, string key, ELoadType loadType, CancellationToken ct)
        {
            var result = await source.AcquireAsync(key, loadType, ct).ConfigureAwait(false);
            return result.Ok ? IssueRef(result.Backing) : _IssueFailedRef(result.Error);
        }

        // 失败凭证: 不进 token 登记表 (无资源可释放), IsValid=false, Error 可读, Dispose 为空操作。
        private ResourceRef _IssueFailedRef(LoadError error)
        {
            var refObj = new ResourceRef();
            refObj.SetupFailed(error);
            return refObj;
        }

        public void ReleaseRef(ResourceRef refObj)
        {
            if (refObj == null)
            {
                return;
            }

            long token = refObj.Token;
            bool removed;
            lock (_refGate)
            {
                removed = _liveRefs.Remove(token);
            }

            // 重复释放检测: token 移除失败则只报错, 绝不做底层释放
            if (!removed)
            {
                Log.Error($"[ResourceSystem] 重复释放凭证: token={token}, address={refObj.Address}");
                return;
            }

            refObj.MarkDisposed();
            refObj.Backing?.Release();   // 多态: 资源型减引用 / 实例型归还池, 无 if kind

            refObj.ResetForPool();
            lock (_refGate)
            {
                _refPool.Return(refObj);
            }
        }

        // 发放一张凭证 (由 LoadRefAsync / InstantiateRefAsync / ResourceRef.AcquireRef 共用)
        internal ResourceRef IssueRef(IRefBacking backing)
        {
            long token = Interlocked.Increment(ref _tokenSeed);
            lock (_refGate)
            {
                var refObj = _refPool.Get();
                refObj.Setup(this, backing, token);
                _liveRefs[token] = refObj;
                return refObj;
            }
        }

        public int LiveRefCount
        {
            get { lock (_refGate) return _liveRefs.Count; }
        }

        // 泄漏检测: DEBUG 强制回收并报错, RELEASE 仅告警
        private void _CheckLeaksOnDispose()
        {
            List<ResourceRef> leaked;
            lock (_refGate)
            {
                if (_liveRefs.Count == 0)
                {
                    return;
                }
                leaked = new List<ResourceRef>(_liveRefs.Values);
            }

#if DEBUG
            Log.Error($"[ResourceSystem] 检测到 {leaked.Count} 个未释放的资源凭证(泄漏), 将强制回收:");
            foreach (var r in leaked)
            {
                Log.Error($"  - token={r.Token}, kind={r.Kind}, address={r.Address}");
                ReleaseRef(r);
            }
#else
            Log.Warn($"[ResourceSystem] 检测到 {leaked.Count} 个未释放的资源凭证(泄漏)");
#endif
        }

        #endregion

        private void _CheckDisposed()
        {
            if (_disposed)
            {
                throw new ResourceException(ELoadError.Disposed, "ResourceManager 已释放, 不可再使用。");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // 泄漏检测要在标记 disposed 前做, 以便强制回收路径正常工作
            _CheckLeaksOnDispose();
            _disposed = true;

            lock (_refGate)
            {
                _liveRefs.Clear();
                _refPool.Clear();
            }

            _instanceSource?.Clear();
            _assetSource.Clear();
        }
    }
}
