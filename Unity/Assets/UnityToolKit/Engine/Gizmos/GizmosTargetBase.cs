/*
 * 功能描述：Gizmos调试目标MonoBehaviour基类
 *           挂载到GameObject上即可自动注册为调试可视化目标
 *           提供常用属性的默认实现，子类可按需重写
 */

using UnityEngine;

namespace UnityToolKit.Engine.Gizmos
{
    /// <summary>
    /// Gizmos调试目标基类
    /// <para>挂载此组件（或其子类）到任意GameObject，即可在Scene视图中进行调试可视化</para>
    /// <para>有形对象自动检测Renderer，无形对象显示占位Gizmo并支持点击交互</para>
    /// </summary>
    [AddComponentMenu("ToolKit/Debug/Gizmos Debug Target")]
    public class GizmosTargetBase : MonoBehaviour, IGizmosDebugTarget
    {
        [Header("调试显示设置")]
        [SerializeField, Tooltip("自定义显示名称，为空则使用GameObject名称")]
        private string _displayName;

        [SerializeField, TextArea(2, 5), Tooltip("详细调试信息，交互时显示")]
        private string _detailInfo;

        [SerializeField, Tooltip("自定义包围盒尺寸（仅在无Renderer和Collider时生效）")]
        private Vector3 _customBoundsSize = Vector3.one * 0.5f;

        /// <summary>
        /// 注册ID，-1表示未注册
        /// </summary>
        private int _registryId = -1;

        private Renderer _cachedRenderer;
        private Collider _cachedCollider;

        /// <summary>
        /// 当前注册ID
        /// </summary>
        public int RegistryId => _registryId;

        #region IGizmosDebugTarget 实现

        public virtual string DebugDisplayName =>
            string.IsNullOrEmpty(_displayName) ? gameObject.name : _displayName;

        public virtual Vector3 DebugWorldPosition => transform.position;

        public virtual bool IsTangible
        {
            get
            {
                if (_cachedRenderer == null)
                    _cachedRenderer = GetComponent<Renderer>();
                return _cachedRenderer != null && _cachedRenderer.enabled;
            }
        }

        public virtual Bounds DebugBounds
        {
            get
            {
                if (_cachedRenderer == null)
                    _cachedRenderer = GetComponent<Renderer>();
                if (_cachedCollider == null)
                    _cachedCollider = GetComponent<Collider>();

                if (_cachedRenderer != null) return _cachedRenderer.bounds;
                if (_cachedCollider != null) return _cachedCollider.bounds;
                return new Bounds(transform.position, _customBoundsSize);
            }
        }

        public virtual string DebugDetailInfo
        {
            get
            {
                if (!string.IsNullOrEmpty(_detailInfo))
                    return _detailInfo;

                return $"名称: {gameObject.name}\n" +
                       $"位置: {transform.position:F2}\n" +
                       $"旋转: {transform.eulerAngles:F1}\n" +
                       $"类型: {(IsTangible ? "有形" : "无形")}\n" +
                       $"层级: {gameObject.layer} ({LayerMask.LayerToName(gameObject.layer)})\n" +
                       $"标签: {gameObject.tag}\n" +
                       $"激活: {gameObject.activeInHierarchy}";
            }
        }

        public virtual bool IsAlive => this != null && gameObject != null;

        public virtual void OnDebugInteract()
        {
            Debug.Log($"[GizmosDebug] 交互目标: [{_registryId:D2}] {DebugDisplayName}\n{DebugDetailInfo}");
        }

        public virtual void OnDrawDebugGizmos() { }

        #endregion

        #region MonoBehaviour 生命周期

        protected virtual void OnEnable()
        {
            _cachedRenderer = GetComponent<Renderer>();
            _cachedCollider = GetComponent<Collider>();
            _registryId = GizmosDebugRegistry.Register(this);
        }

        protected virtual void OnDisable()
        {
            if (_registryId >= 0)
            {
                GizmosDebugRegistry.Unregister(_registryId);
                _registryId = -1;
            }
        }

        #endregion

        #region 编辑器辅助

        /// <summary>
        /// 手动刷新缓存的组件引用
        /// </summary>
        public void RefreshCachedComponents()
        {
            _cachedRenderer = GetComponent<Renderer>();
            _cachedCollider = GetComponent<Collider>();
        }

        /// <summary>
        /// 设置自定义显示名称
        /// </summary>
        public void SetDisplayName(string name)
        {
            _displayName = name;
        }

        /// <summary>
        /// 设置自定义详细信息
        /// </summary>
        public void SetDetailInfo(string info)
        {
            _detailInfo = info;
        }

        #endregion
    }
}
