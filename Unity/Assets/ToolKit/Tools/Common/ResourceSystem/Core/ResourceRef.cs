/*
 * author       : Gordon
 * datetime     : 2026/6/27
 * description  : 资源引用凭证 (引擎无关), 业务层持有的资源使用凭证, 位于 AssetHandle 之上。
 *                - 只持有一个 IRefBacking + token, 不再区分资源型/实例型 —— 行为差异全在背书里 (多态);
 *                - 实现 IDisposable, 支持 using; Dispose 由 ResourceManager 按 token 校验后释放背书;
 *                - 可被 ObjectPool 池化; Dispose/回池后置位并清空, 防止复用后误用。
 */

using System;

namespace ToolKit.Tools.Common
{
    public sealed class ResourceRef : IDisposable
    {
        private ResourceManager _owner;
        private IRefBacking _backing;
        private long _token;
        private bool _disposed = true;

        public long Token => _token;
        public ERefKind Kind => _backing?.Kind ?? ERefKind.Asset;
        public string Address => _backing?.Address;

        internal IRefBacking Backing => _backing;

        /// <summary> 凭证是否有效: 未释放且底层资源/实例存活 </summary>
        public bool IsValid => !_disposed && _backing != null && _backing.IsAlive;

        /// <summary> 取对象。已释放/已销毁返回 null。 </summary>
        public T Get<T>() where T : class
        {
            if (_disposed)
            {
                Log.Error("[ResourceSystem] 凭证已释放, 不可再 Get (可能在 using 块外继续使用了已回收的 ResourceRef)");
                return null;
            }
            return _backing?.Get<T>();
        }

        /// <summary>
        /// 复制出一份独立凭证。资源型支持 (各自独立释放); 实例型不支持 (需重新 InstantiateRefAsync)。
        /// </summary>
        public ResourceRef AcquireRef()
        {
            if (_disposed || _owner == null || _backing == null)
            {
                Log.Error("[ResourceSystem] 凭证无效, 无法 AcquireRef");
                return null;
            }
            var clone = _backing.AcquireClone();
            if (clone == null)
            {
                Log.Error("[ResourceSystem] 该来源不支持复制凭证 (实例型请重新 InstantiateRefAsync)");
                return null;
            }
            return _owner.IssueRef(clone);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _owner?.ReleaseRef(this);
        }

        #region 由 ResourceManager 调用

        internal void Setup(ResourceManager owner, IRefBacking backing, long token)
        {
            _owner = owner;
            _backing = backing;
            _token = token;
            _disposed = false;
        }

        internal void MarkDisposed()
        {
            _disposed = true;
        }

        internal void ResetForPool()
        {
            _owner = null;
            _backing = null;
            _token = 0;
            _disposed = true;
        }

        #endregion
    }
}
