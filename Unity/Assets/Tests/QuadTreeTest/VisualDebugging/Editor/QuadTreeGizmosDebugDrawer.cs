/*
 * 功能描述：四叉树Scene视图可视化绘制器 + 自定义Inspector
 *           在Scene视图中绘制四叉树的网格结构、深度热力图、元素数量标签
 *           提供自定义Inspector面板显示运行时状态和深度颜色图例
 */

using System.Collections.Generic;
using ToolKit.DataStructure;
using UnityEditor;
using UnityEngine;
using UnityToolKit.Engine.Gizmos;

namespace Tests.QuadTreeTest
{
    /// <summary>
    /// 四叉树Scene视图绘制器
    /// <para>通过 SceneView.duringSceneGui 在Scene视图中绘制所有已注册的四叉树调试目标</para>
    /// <para>绘制内容：节点边界网格、深度颜色编码、元素密度热力图、元素数量标签、统计信息</para>
    /// </summary>
    [InitializeOnLoad]
    public static class QuadTreeGizmosDebugDrawer
    {
        #region 深度颜色调板

        /// <summary>
        /// 深度颜色调板（从浅到深，索引对应树深度）
        /// </summary>
        public static readonly Color[] DepthColors = new Color[]
        {
            new Color(0.30f, 0.90f, 0.30f), // 深度0 - 绿色
            new Color(0.30f, 0.70f, 0.90f), // 深度1 - 天蓝
            new Color(0.30f, 0.30f, 0.90f), // 深度2 - 蓝色
            new Color(0.70f, 0.30f, 0.90f), // 深度3 - 紫色
            new Color(0.90f, 0.30f, 0.70f), // 深度4 - 品红
            new Color(0.90f, 0.30f, 0.30f), // 深度5 - 红色
            new Color(0.90f, 0.60f, 0.30f), // 深度6 - 橙色
            new Color(0.90f, 0.90f, 0.30f), // 深度7 - 黄色
            new Color(1.00f, 1.00f, 1.00f), // 深度8+ - 白色
        };

        /// <summary>
        /// 热力图低密度颜色
        /// </summary>
        private static readonly Color HeatmapLow = new Color(0.2f, 0.5f, 1f, 0.06f);

        /// <summary>
        /// 热力图高密度颜色
        /// </summary>
        private static readonly Color HeatmapHigh = new Color(1f, 0.15f, 0.15f, 0.25f);

        #endregion

        #region GUI样式

        private static GUIStyle _countLabelStyle;
        private static GUIStyle _statsLabelStyle;

        #endregion

        static QuadTreeGizmosDebugDrawer()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private static void EnsureStyles()
        {
            if (_countLabelStyle == null)
            {
                _countLabelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 10,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.9f) },
                };
            }

            if (_statsLabelStyle == null)
            {
                _statsLabelStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    fontSize = 11,
                    padding = new RectOffset(8, 8, 4, 4),
                    richText = true,
                    normal = { textColor = Color.white },
                };
            }
        }

        #region Scene视图绘制入口

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!GizmosDebugRegistry.GlobalEnabled) return;

            var targets = Object.FindObjectsOfType<QuadTreeGizmosDebugTarget>();
            if (targets == null || targets.Length == 0) return;

            EnsureStyles();

            foreach (var target in targets)
            {
                if (target.TreeInfo == null) continue;
                if (!target.isActiveAndEnabled) continue;

                DrawQuadTree(target, sceneView);
            }

            // 持续刷新Scene视图以反映树的实时变化
            sceneView.Repaint();
        }

        #endregion

        #region 四叉树绘制

        /// <summary>
        /// 绘制单个四叉树的完整可视化
        /// </summary>
        private static void DrawQuadTree(QuadTreeGizmosDebugTarget target, SceneView sceneView)
        {
            target.RefreshNodeCache();
            var nodes = target.CachedNodes;
            if (nodes.Count == 0) return;

            // 第一遍：找最大值数量（用于热力图归一化）
            int maxValuesInNode = 0;
            foreach (var node in nodes)
            {
                if (node.ValueCount > maxValuesInNode)
                    maxValuesInNode = node.ValueCount;
            }

            // 第二遍：绘制所有节点
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (target.LeafOnly && !node.IsLeaf) continue;

                // --- 深度热力填充（先画填充，再画边框，避免遮挡） ---
                if (target.ShowDepthHeatmap && node.IsLeaf && node.ValueCount > 0)
                {
                    float t = maxValuesInNode > 1
                        ? (float)node.ValueCount / maxValuesInNode
                        : 0f;
                    Color fillColor = Color.Lerp(HeatmapLow, HeatmapHigh, t);
                    DrawFilledRect2D(target, node.Box, fillColor);
                }

                // --- 网格边界线 ---
                if (target.ShowGrid)
                {
                    Color lineColor;
                    float lineWidth;

                    if (node.Depth == 0)
                    {
                        lineColor = target.RootColor;
                        lineWidth = target.GridLineWidth * 2f;
                    }
                    else
                    {
                        lineColor = GetDepthColor(node.Depth);
                        lineColor.a = target.GridAlpha;
                        lineWidth = target.GridLineWidth;
                    }

                    Handles.color = lineColor;
                    DrawWireRect2D(target, node.Box, lineWidth);
                }

                // --- 元素数量标签 ---
                if (target.ShowValueCount && node.ValueCount > 0)
                {
                    DrawValueCountLabel(target, node);
                }
            }

            // 统计信息覆盖层
            DrawStatsOverlay(target, sceneView);

            // 绘制元素包围盒
            if (target.ShowElements)
            {
                DrawElements(target);
            }
        }

        /// <summary>
        /// 绘制节点中的元素数量标签
        /// </summary>
        private static void DrawValueCountLabel(QuadTreeGizmosDebugTarget target, QuadTreeNodeDebugInfo node)
        {
            var center3D = target.MapBoxCenterToWorld(node.Box);
            string label = node.ValueCount.ToString();

            Handles.BeginGUI();
            {
                Vector2 screenPos = HandleUtility.WorldToGUIPoint(center3D);
                var content = new GUIContent(label);
                var size = _countLabelStyle.CalcSize(content);

                // 添加内边距
                size.x += 6f;
                size.y += 2f;

                var rect = new Rect(
                    screenPos.x - size.x * 0.5f,
                    screenPos.y - size.y * 0.5f,
                    size.x,
                    size.y
                );

                // 背景（深色半透明）
                EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.55f));

                // 左侧深度颜色指示条
                var depthColor = GetDepthColor(node.Depth);
                var colorBar = new Rect(rect.x, rect.y, 3f, rect.height);
                EditorGUI.DrawRect(colorBar, depthColor);

                // 文字
                GUI.Label(rect, label, _countLabelStyle);
            }
            Handles.EndGUI();
        }

        /// <summary>
        /// 在Scene视图底部绘制统计信息
        /// </summary>
        private static void DrawStatsOverlay(QuadTreeGizmosDebugTarget target, SceneView sceneView)
        {
            var info = target.TreeInfo;
            if (info == null) return;

            var nodes = target.CachedNodes;
            int totalNodes = nodes.Count;
            int leafNodes = 0;
            int maxDepth = 0;
            int nodesWithValues = 0;

            foreach (var node in nodes)
            {
                if (node.IsLeaf) leafNodes++;
                if (node.Depth > maxDepth) maxDepth = node.Depth;
                if (node.ValueCount > 0) nodesWithValues++;
            }

            string stats =
                $"<b>{target.DebugDisplayName}</b>  " +
                $"元素: <b>{info.ElementCount}</b>  " +
                $"节点: <b>{totalNodes}</b> (叶: {leafNodes})  " +
                $"深度: <b>{maxDepth}</b>/{info.ConfigMaxDepth}";

            Handles.BeginGUI();
            {
                var content = new GUIContent(stats);
                float width = 520f;
                float height = _statsLabelStyle.CalcHeight(content, width) + 4f;
                var rect = new Rect(10, sceneView.position.height - height - 30, width, height);

                EditorGUI.DrawRect(rect, new Color(0.1f, 0.1f, 0.1f, 0.85f));

                // 左侧颜色条
                var colorBar = new Rect(rect.x, rect.y, 4f, rect.height);
                EditorGUI.DrawRect(colorBar, new Color(0.3f, 0.9f, 0.3f));

                var labelRect = new Rect(rect.x + 8, rect.y, rect.width - 8, rect.height);
                GUI.Label(labelRect, stats, _statsLabelStyle);
            }
            Handles.EndGUI();
        }

        #endregion

        #region 几何绘制辅助

        /// <summary>
        /// 绘制所有元素的包围盒
        /// </summary>
        private static void DrawElements(QuadTreeGizmosDebugTarget target)
        {
            var elements = target.CachedElementBoxes;
            if (elements.Count == 0) return;

            var elemFillColor = new Color(0.3f, 0.9f, 1f, 0.15f);
            var elemLineColor = new Color(0.3f, 0.9f, 1f, 0.7f);
            const float elemLineWidth = 1.5f;

            for (int i = 0; i < elements.Count; i++)
            {
                var box = elements[i];

                // 填充
                DrawFilledRect2D(target, box, elemFillColor);

                // 边框
                Handles.color = elemLineColor;
                DrawWireRect2D(target, box, elemLineWidth);

                // 中心点标记
                var center = target.MapBoxCenterToWorld(box);
                Handles.color = elemLineColor;
                float dotSize = HandleUtility.GetHandleSize(center) * 0.03f;
                Handles.DotHandleCap(0, center, Quaternion.identity, dotSize, EventType.Repaint);
            }
        }

        /// <summary>
        /// 获取深度对应的颜色
        /// </summary>
        public static Color GetDepthColor(int depth)
        {
            if (depth < 0) depth = 0;
            return DepthColors[Mathf.Min(depth, DepthColors.Length - 1)];
        }

        /// <summary>
        /// 在目标映射平面上绘制2D矩形线框
        /// </summary>
        private static void DrawWireRect2D(QuadTreeGizmosDebugTarget target, AABBBox box, float lineWidth)
        {
            Vector3 bl = target.MapToWorld(box.Left, box.Bottom);
            Vector3 br = target.MapToWorld(box.Right, box.Bottom);
            Vector3 tr = target.MapToWorld(box.Right, box.Top);
            Vector3 tl = target.MapToWorld(box.Left, box.Top);

            Handles.DrawAAPolyLine(lineWidth, bl, br);
            Handles.DrawAAPolyLine(lineWidth, br, tr);
            Handles.DrawAAPolyLine(lineWidth, tr, tl);
            Handles.DrawAAPolyLine(lineWidth, tl, bl);
        }

        /// <summary>
        /// 在目标映射平面上绘制2D填充矩形
        /// </summary>
        private static void DrawFilledRect2D(QuadTreeGizmosDebugTarget target, AABBBox box, Color color)
        {
            Vector3 bl = target.MapToWorld(box.Left, box.Bottom);
            Vector3 br = target.MapToWorld(box.Right, box.Bottom);
            Vector3 tr = target.MapToWorld(box.Right, box.Top);
            Vector3 tl = target.MapToWorld(box.Left, box.Top);

            var oldColor = Handles.color;
            Handles.color = color;
            Handles.DrawAAConvexPolygon(bl, tl, tr, br);
            Handles.color = oldColor;
        }

        #endregion
    }

    /// <summary>
    /// QuadTreeGizmosDebugTarget 的自定义Inspector
    /// <para>显示运行时状态、统计信息和深度颜色图例</para>
    /// </summary>
    [CustomEditor(typeof(QuadTreeGizmosDebugTarget))]
    public class QuadTreeGizmosDebugTargetInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var target = (QuadTreeGizmosDebugTarget)this.target;
            EditorGUILayout.Space(8);

            // === 运行时状态 ===
            EditorGUILayout.LabelField("运行时状态", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "请在Play模式下调用 SetTarget(IQuadTreeDebugInfo) 连接四叉树实例。\n" +
                    "QuadTree<T> 已实现 IQuadTreeDebugInfo 接口，可直接传入。",
                    MessageType.Info);
            }
            else if (target.TreeInfo == null)
            {
                EditorGUILayout.HelpBox(
                    "未连接四叉树实例。\n请在代码中调用 SetTarget() 方法设置要调试的四叉树。",
                    MessageType.Warning);
            }
            else
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.IntField("元素数量", target.TreeInfo.ElementCount);
                EditorGUILayout.IntField("配置最大深度", target.TreeInfo.ConfigMaxDepth);
                EditorGUILayout.IntField("配置分裂阈值", target.TreeInfo.ConfigValueThreshold);
                EditorGUILayout.LabelField("根节点范围", target.TreeInfo.RootBox.ToString());
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.Space(4);

                EditorGUILayout.BeginHorizontal();
                {
                    if (GUILayout.Button("刷新缓存", GUILayout.Height(24)))
                    {
                        target.RefreshNodeCache();
                        SceneView.RepaintAll();
                    }

                    if (GUILayout.Button("输出树信息到Console", GUILayout.Height(24)))
                    {
                        target.OnDebugInteract();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            // === 深度颜色图例 ===
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("深度颜色图例", EditorStyles.boldLabel);
            DrawDepthColorLegend();
        }

        /// <summary>
        /// 绘制深度颜色图例
        /// </summary>
        private void DrawDepthColorLegend()
        {
            EditorGUILayout.BeginHorizontal();
            {
                for (int i = 0; i < QuadTreeGizmosDebugDrawer.DepthColors.Length; i++)
                {
                    var rect = GUILayoutUtility.GetRect(28, 20, GUILayout.Width(28));
                    EditorGUI.DrawRect(rect, QuadTreeGizmosDebugDrawer.DepthColors[i]);

                    string label = i < QuadTreeGizmosDebugDrawer.DepthColors.Length - 1
                        ? i.ToString()
                        : $"{i}+";

                    var labelStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = i >= 5 ? Color.black : Color.white }
                    };

                    GUI.Label(rect, label, labelStyle);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(
                "绿 → 蓝 → 紫 → 红 → 橙 → 黄 → 白（深度递增）",
                EditorStyles.miniLabel);
        }
    }
}
