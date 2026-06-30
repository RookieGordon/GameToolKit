/*
 * 功能描述：四叉树Gizmos调试可视化组件
 *           挂载到任意GameObject上，通过SetTarget()设置要调试的四叉树实例
 *           自动注册到GizmosDebugRegistry，在Scene视图中显示四叉树结构
 */

using System.Collections.Generic;
using ToolKit.DataStructure;
using UnityEngine;
using UnityToolKit.Engine.Gizmos;

namespace Tests.QuadTreeTest
{
    /// <summary>
    /// 2D四叉树映射到3D场景的坐标平面
    /// </summary>
    public enum QuadTreePlaneMapping
    {
        /// <summary>
        /// float2.x → X, float2.y → Z（俯视 / 地面平面，适合大多数3D游戏）
        /// </summary>
        XZ,

        /// <summary>
        /// float2.x → X, float2.y → Y（侧视平面，适合2D游戏）
        /// </summary>
        XY,
    }

    /// <summary>
    /// 四叉树Gizmos调试可视化组件
    /// <para>挂载到GameObject上并在运行时调用 <see cref="SetTarget"/> 设置四叉树，即可在Scene视图中可视化树结构</para>
    /// <para>自动接入Gizmos Debug Framework，可通过Gizmos Debug Window统一管理开关和交互</para>
    /// </summary>
    [AddComponentMenu("ToolKit/Debug/QuadTree Gizmos Debugger")]
    public class QuadTreeGizmosDebugTarget : MonoBehaviour, IGizmosDebugTarget
    {
        #region 序列化配置

        [Header("基本设置")]
        [SerializeField, Tooltip("调试显示名称")]
        private string _displayName = "QuadTree";

        [SerializeField, Tooltip("2D四叉树映射到3D场景的坐标平面")]
        private QuadTreePlaneMapping _planeMapping = QuadTreePlaneMapping.XZ;

        [SerializeField, Tooltip("映射平面的偏移量（XZ模式为Y坐标，XY模式为Z坐标）")]
        private float _planeOffset = 0f;

        [Header("可视化开关")]
        [SerializeField, Tooltip("显示节点边界网格线")]
        private bool _showGrid = true;

        [SerializeField, Tooltip("显示节点中的元素数量标签")]
        private bool _showValueCount = true;

        [SerializeField, Tooltip("显示深度热力图（叶子节点按元素密度着色）")]
        private bool _showDepthHeatmap = true;

        [SerializeField, Tooltip("显示元素包围盒")]
        private bool _showElements = true;

        [SerializeField, Tooltip("仅显示叶子节点")]
        private bool _leafOnly = false;

        [Header("外观设置")]
        [SerializeField, Range(0.5f, 5f), Tooltip("网格线宽度")]
        private float _gridLineWidth = 1.5f;

        [SerializeField, Range(0.1f, 1f), Tooltip("网格线透明度")]
        private float _gridAlpha = 0.6f;

        [SerializeField, Tooltip("根节点边框颜色")]
        private Color _rootColor = new Color(1f, 1f, 1f, 0.8f);

        #endregion

        #region 内部状态

        private int _registryId = -1;
        private IQuadTreeDebugInfo _treeInfo;
        private readonly List<QuadTreeNodeDebugInfo> _nodeCache = new List<QuadTreeNodeDebugInfo>();
        private readonly List<AABBBox> _elementBoxCache = new List<AABBBox>();

        #endregion

        #region 公开属性（供Editor绘制器读取）

        public IQuadTreeDebugInfo TreeInfo => _treeInfo;
        public QuadTreePlaneMapping PlaneMapping => _planeMapping;
        public float PlaneOffset => _planeOffset;
        public bool ShowGrid => _showGrid;
        public bool ShowValueCount => _showValueCount;
        public bool ShowDepthHeatmap => _showDepthHeatmap;
        public bool ShowElements => _showElements;
        public bool LeafOnly => _leafOnly;
        public float GridLineWidth => _gridLineWidth;
        public float GridAlpha => _gridAlpha;
        public Color RootColor => _rootColor;
        public IReadOnlyList<QuadTreeNodeDebugInfo> CachedNodes => _nodeCache;
        public IReadOnlyList<AABBBox> CachedElementBoxes => _elementBoxCache;

        #endregion

        #region 公开API

        /// <summary>
        /// 设置要调试的四叉树实例
        /// <para>QuadTree&lt;T&gt; 已实现 IQuadTreeDebugInfo 接口，可直接传入</para>
        /// </summary>
        /// <example>
        /// <code>
        /// var tree = new QuadTree&lt;MyUnit&gt;(new AABBBox(0, 0, 100, 100));
        /// GetComponent&lt;QuadTreeGizmosDebugTarget&gt;().SetTarget(tree);
        /// </code>
        /// </example>
        public void SetTarget(IQuadTreeDebugInfo treeInfo)
        {
            _treeInfo = treeInfo;
        }

        /// <summary>
        /// 清除调试目标
        /// </summary>
        public void ClearTarget()
        {
            _treeInfo = null;
            _nodeCache.Clear();
            _elementBoxCache.Clear();
        }

        /// <summary>
        /// 刷新节点缓存（由Editor绘制器每帧调用）
        /// </summary>
        public void RefreshNodeCache()
        {
            if (_treeInfo == null)
            {
                _nodeCache.Clear();
                return;
            }

            _treeInfo.CollectDebugNodeInfos(_nodeCache);
            _treeInfo.CollectDebugElementBoxes(_elementBoxCache);
        }

        /// <summary>
        /// 将2D坐标映射到3D世界坐标
        /// </summary>
        public Vector3 MapToWorld(float x, float y)
        {
            return _planeMapping == QuadTreePlaneMapping.XZ
                ? new Vector3(x, _planeOffset, y)
                : new Vector3(x, y, _planeOffset);
        }

        /// <summary>
        /// 将AABBBox中心映射到3D世界坐标
        /// </summary>
        public Vector3 MapBoxCenterToWorld(AABBBox box)
        {
            return MapToWorld(box.Center.x, box.Center.y);
        }

        #endregion

        #region IGizmosDebugTarget 实现

        public string DebugDisplayName =>
            string.IsNullOrEmpty(_displayName) ? "QuadTree" : _displayName;

        public Vector3 DebugWorldPosition
        {
            get
            {
                if (_treeInfo == null) return transform.position;
                return MapBoxCenterToWorld(_treeInfo.RootBox);
            }
        }

        public bool IsTangible => false;

        public Bounds DebugBounds
        {
            get
            {
                if (_treeInfo == null)
                    return new Bounds(transform.position, Vector3.one);

                var box = _treeInfo.RootBox;
                var center = MapBoxCenterToWorld(box);
                Vector3 size;
                if (_planeMapping == QuadTreePlaneMapping.XZ)
                    size = new Vector3(box.Width, 0.1f, box.Height);
                else
                    size = new Vector3(box.Width, box.Height, 0.1f);

                return new Bounds(center, size);
            }
        }

        public string DebugDetailInfo
        {
            get
            {
                if (_treeInfo == null)
                    return "未设置四叉树目标\n请调用 SetTarget() 设置四叉树实例";

                RefreshNodeCache();
                int totalNodes = _nodeCache.Count;
                int leafNodes = 0;
                int maxDepth = 0;
                int maxValues = 0;
                int nodesWithValues = 0;

                foreach (var node in _nodeCache)
                {
                    if (node.IsLeaf) leafNodes++;
                    if (node.Depth > maxDepth) maxDepth = node.Depth;
                    if (node.ValueCount > maxValues) maxValues = node.ValueCount;
                    if (node.ValueCount > 0) nodesWithValues++;
                }

                return $"元素总数: {_treeInfo.ElementCount}\n" +
                       $"节点总数: {totalNodes}  (叶子: {leafNodes})\n" +
                       $"含值节点: {nodesWithValues}\n" +
                       $"当前最大深度: {maxDepth}\n" +
                       $"单节点最大元素数: {maxValues}\n" +
                       $"─────────────────\n" +
                       $"配置上限深度: {_treeInfo.ConfigMaxDepth}\n" +
                       $"配置分裂阈值: {_treeInfo.ConfigValueThreshold}\n" +
                       $"根节点范围: {_treeInfo.RootBox}\n" +
                       $"坐标映射: {_planeMapping}  偏移: {_planeOffset:F1}";
            }
        }

        public bool IsAlive => this != null && gameObject != null;

        public void OnDebugInteract()
        {
            Debug.Log($"[QuadTreeDebug] {DebugDisplayName}\n{DebugDetailInfo}");
        }

        /// <summary>
        /// 自定义Gizmo绘制留空，由专门的Editor绘制器 <see cref="QuadTreeGizmosDebugDrawer"/> 处理
        /// </summary>
        public void OnDrawDebugGizmos() { }

        #endregion

        #region MonoBehaviour 生命周期

        private void OnEnable()
        {
            _registryId = GizmosDebugRegistry.Register(this);
        }

        private void OnDisable()
        {
            if (_registryId >= 0)
            {
                GizmosDebugRegistry.Unregister(_registryId);
                _registryId = -1;
            }
        }

        private void OnDestroy()
        {
            ClearTarget();
        }

        #endregion
    }
}
