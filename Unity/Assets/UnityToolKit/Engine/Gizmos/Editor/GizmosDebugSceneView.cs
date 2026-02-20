/*
 * 功能描述：Gizmos调试Scene视图绘制器
 *           在Scene视图中绘制3D标签、包围盒线框、占位Gizmo
 *           处理无形对象的点击交互
 */

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityToolKit.Engine.Gizmos;

namespace UnityToolKit.Editor.Gizmos
{
    /// <summary>
    /// Scene视图调试可视化绘制器
    /// <para>通过 SceneView.duringSceneGui 在Scene视图中绘制所有已注册的调试目标</para>
    /// </summary>
    [InitializeOnLoad]
    public static class GizmosDebugSceneView
    {
        /// <summary>
        /// 当前选中的调试条目ID（-1表示无选中）
        /// </summary>
        public static int SelectedEntryId { get; set; } = -1;

        /// <summary>
        /// 当前悬停的调试条目ID
        /// </summary>
        public static int HoveredEntryId { get; private set; } = -1;

        /// <summary>
        /// 是否显示信息浮窗
        /// </summary>
        public static bool ShowInfoPopup { get; set; } = false;

        /// <summary>
        /// 选中变更事件
        /// </summary>
        public static event System.Action<int> OnSelectionChanged;

        private static GUIStyle _labelStyle;
        private static GUIStyle _labelBgStyle;
        private static GUIStyle _infoBoxStyle;
        private static GUIStyle _infoTitleStyle;

        private static readonly float HandlePickSize = 0.8f;
        private static readonly float IntangibleIconSize = 0.35f;

        static GizmosDebugSceneView()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
        }

        /// <summary>
        /// 手动启用Scene视图绘制（EditorWindow打开时调用）
        /// </summary>
        public static void Enable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
        }

        /// <summary>
        /// 手动禁用Scene视图绘制
        /// </summary>
        public static void Disable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        private static void EnsureStyles()
        {
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = GizmosDebugRegistry.LabelFontSize,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white },
                    padding = new RectOffset(4, 4, 2, 2),
                };
            }
            else
            {
                _labelStyle.fontSize = GizmosDebugRegistry.LabelFontSize;
            }

            if (_labelBgStyle == null)
            {
                _labelBgStyle = new GUIStyle("box")
                {
                    fontSize = GizmosDebugRegistry.LabelFontSize,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white },
                    padding = new RectOffset(6, 6, 3, 3),
                };
            }
            else
            {
                _labelBgStyle.fontSize = GizmosDebugRegistry.LabelFontSize;
            }

            if (_infoBoxStyle == null)
            {
                _infoBoxStyle = new GUIStyle("helpBox")
                {
                    fontSize = 12,
                    padding = new RectOffset(10, 10, 8, 8),
                    richText = true,
                    wordWrap = true,
                };
            }

            if (_infoTitleStyle == null)
            {
                _infoTitleStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 13,
                    normal = { textColor = new Color(0.9f, 0.9f, 0.2f) },
                };
            }
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!GizmosDebugRegistry.GlobalEnabled)
                return;

            if (GizmosDebugRegistry.Count == 0)
                return;

            EnsureStyles();

            // 定期清理失效条目
            GizmosDebugRegistry.Cleanup();

            var entries = GizmosDebugRegistry.Entries;
            var sortedEntries = new List<GizmosDebugEntry>();
            foreach (var kvp in entries)
            {
                if (kvp.Value.Enabled && kvp.Value.Target != null && kvp.Value.Target.IsAlive)
                    sortedEntries.Add(kvp.Value);
            }

            HoveredEntryId = -1;

            foreach (var entry in sortedEntries)
            {
                DrawEntry(entry, sceneView);
            }

            // 绘制选中目标的信息浮窗
            if (ShowInfoPopup && SelectedEntryId >= 0)
            {
                DrawInfoPopup(sceneView);
            }

            // 强制刷新Scene视图
            if (sortedEntries.Count > 0)
                sceneView.Repaint();
        }

        /// <summary>
        /// 绘制单个调试条目
        /// </summary>
        private static void DrawEntry(GizmosDebugEntry entry, SceneView sceneView)
        {
            var target = entry.Target;
            var position = target.DebugWorldPosition;
            var bounds = target.DebugBounds;
            var color = entry.Color;
            bool isSelected = entry.Id == SelectedEntryId;
            bool isTangible = target.IsTangible;

            // === 1. 绘制包围盒线框 ===
            if (GizmosDebugRegistry.ShowBounds)
            {
                var boundsColor = color;
                boundsColor.a = isSelected ? 0.8f : 0.4f;
                Handles.color = boundsColor;
                DrawWireBounds(bounds, isSelected ? 2f : 1f);
            }

            // === 2. 无形对象：绘制占位Gizmo和交互区 ===
            if (!isTangible)
            {
                DrawIntangibleGizmo(entry, position, isSelected, sceneView);
            }

            // === 3. 绘制3D标签 ===
            DrawLabel(entry, position, bounds, sceneView);

            // === 4. 有形对象选中时的高亮 ===
            if (isTangible && isSelected)
            {
                var highlightColor = color;
                highlightColor.a = 0.15f;
                Handles.color = highlightColor;
                Handles.DrawSolidDisc(position, sceneView.camera.transform.forward, bounds.extents.magnitude * 0.5f);
            }

            // === 5. 连接线 ===
            if (GizmosDebugRegistry.ShowConnectors)
            {
                var labelPos = GetLabelWorldPosition(position, bounds);
                var lineColor = color;
                lineColor.a = 0.3f;
                Handles.color = lineColor;
                Handles.DrawDottedLine(position, labelPos, 3f);
            }

            // === 6. 自定义绘制回调 ===
            target.OnDrawDebugGizmos();
        }

        /// <summary>
        /// 绘制无形对象的占位Gizmo和交互手柄
        /// </summary>
        private static void DrawIntangibleGizmo(GizmosDebugEntry entry, Vector3 position, bool isSelected, SceneView sceneView)
        {
            var color = entry.Color;
            float size = GizmosDebugRegistry.GizmoSize;

            // 绘制菱形/十字星占位图标
            Handles.color = color;
            float iconSize = size * IntangibleIconSize;

            // 绘制三轴线交叉
            Handles.DrawLine(position - Vector3.right * iconSize, position + Vector3.right * iconSize);
            Handles.DrawLine(position - Vector3.up * iconSize, position + Vector3.up * iconSize);
            Handles.DrawLine(position - Vector3.forward * iconSize, position + Vector3.forward * iconSize);

            // 绘制线框球
            var sphereColor = color;
            sphereColor.a = isSelected ? 0.6f : 0.3f;
            Handles.color = sphereColor;
            Handles.DrawWireDisc(position, sceneView.camera.transform.forward, size * 0.3f);
            Handles.DrawWireDisc(position, sceneView.camera.transform.up, size * 0.3f);
            Handles.DrawWireDisc(position, sceneView.camera.transform.right, size * 0.3f);

            // 选中时绘制实心半透明圆
            if (isSelected)
            {
                var solidColor = color;
                solidColor.a = 0.12f;
                Handles.color = solidColor;
                Handles.DrawSolidDisc(position, sceneView.camera.transform.forward, size * 0.35f);
            }

            // 可点击手柄（Button）
            Handles.color = color;
            float handleSize = HandleUtility.GetHandleSize(position) * HandlePickSize;
            if (Handles.Button(position, Quaternion.identity, size * 0.05f, handleSize * 0.5f, Handles.DotHandleCap))
            {
                if (SelectedEntryId == entry.Id)
                {
                    // 双击同一目标：触发交互
                    ShowInfoPopup = !ShowInfoPopup;
                    entry.Target.OnDebugInteract();
                }
                else
                {
                    SelectedEntryId = entry.Id;
                    ShowInfoPopup = true;
                    OnSelectionChanged?.Invoke(entry.Id);
                }

                // 如果目标关联MonoBehaviour，在Hierarchy中选中
                if (entry.Target is Component comp && comp != null)
                {
                    Selection.activeGameObject = comp.gameObject;
                    EditorGUIUtility.PingObject(comp.gameObject);
                }
            }

            // 悬停检测
            float distToMouse = HandleUtility.DistanceToCircle(position, size * 0.3f);
            if (distToMouse < 10f)
            {
                HoveredEntryId = entry.Id;

                // 绘制悬停高亮环
                var hoverColor = Color.white;
                hoverColor.a = 0.5f;
                Handles.color = hoverColor;
                Handles.DrawWireDisc(position, sceneView.camera.transform.forward, size * 0.4f);
            }
        }

        /// <summary>
        /// 绘制3D标签（编号+名称）
        /// </summary>
        private static void DrawLabel(GizmosDebugEntry entry, Vector3 position, Bounds bounds, SceneView sceneView)
        {
            var labelPos = GetLabelWorldPosition(position, bounds);
            string labelText = $"[{entry.Id:D2}] {entry.Target.DebugDisplayName}";

            bool isSelected = entry.Id == SelectedEntryId;
            bool isHovered = entry.Id == HoveredEntryId;

            // 标签背景色
            var bgColor = entry.Color;
            bgColor.a = isSelected ? 0.85f : (isHovered ? 0.65f : 0.45f);

            // 使用Handles.GUI绘制带背景的标签
            Handles.BeginGUI();
            {
                Vector2 screenPos = HandleUtility.WorldToGUIPoint(labelPos);
                var content = new GUIContent(labelText);
                var labelSize = _labelBgStyle.CalcSize(content);

                var rect = new Rect(
                    screenPos.x - labelSize.x * 0.5f,
                    screenPos.y - labelSize.y * 0.5f,
                    labelSize.x,
                    labelSize.y
                );

                // 绘制背景
                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = bgColor;
                GUI.Box(rect, GUIContent.none, _labelBgStyle);
                GUI.backgroundColor = oldBg;

                // 绘制文字
                _labelStyle.normal.textColor = isSelected ? Color.white : new Color(1f, 1f, 1f, 0.95f);
                GUI.Label(rect, labelText, _labelStyle);

                // 无形对象标记
                if (!entry.Target.IsTangible)
                {
                    var iconRect = new Rect(rect.xMax + 2, rect.y + 2, 16, 16);
                    var oldColor = GUI.color;
                    GUI.color = entry.Color;
                    GUI.Label(iconRect, "◆", EditorStyles.miniLabel);
                    GUI.color = oldColor;
                }
            }
            Handles.EndGUI();
        }

        /// <summary>
        /// 绘制选中目标的信息浮窗
        /// </summary>
        private static void DrawInfoPopup(SceneView sceneView)
        {
            if (!GizmosDebugRegistry.TryGetEntry(SelectedEntryId, out var entry))
            {
                ShowInfoPopup = false;
                return;
            }

            if (entry.Target == null || !entry.Target.IsAlive)
            {
                ShowInfoPopup = false;
                return;
            }

            var position = entry.Target.DebugWorldPosition;

            Handles.BeginGUI();
            {
                Vector2 screenPos = HandleUtility.WorldToGUIPoint(position);

                float popupWidth = 280f;
                float popupX = screenPos.x + 30f;
                float popupY = screenPos.y - 20f;

                // 标题
                string title = $"[{entry.Id:D2}] {entry.Target.DebugDisplayName}";
                string typeStr = entry.Target.IsTangible ? "有形对象" : "无形对象（可交互）";
                string info = entry.Target.DebugDetailInfo ?? "无详细信息";

                string fullText = $"<b>{title}</b>\n<color=#AAAAAA>类型: {typeStr}</color>\n\n{info}";

                var content = new GUIContent(fullText);
                float popupHeight = _infoBoxStyle.CalcHeight(content, popupWidth - 20f) + 35f;

                var popupRect = new Rect(popupX, popupY, popupWidth, popupHeight);

                // 确保浮窗不超出屏幕
                if (popupRect.xMax > sceneView.position.width - 10)
                    popupRect.x = screenPos.x - popupWidth - 30f;
                if (popupRect.yMax > sceneView.position.height - 10)
                    popupRect.y = sceneView.position.height - popupHeight - 10;
                if (popupRect.y < 10)
                    popupRect.y = 10;

                // 背景
                var bgColor = new Color(0.15f, 0.15f, 0.15f, 0.92f);
                EditorGUI.DrawRect(popupRect, bgColor);

                // 颜色条
                var colorBarRect = new Rect(popupRect.x, popupRect.y, 4f, popupRect.height);
                EditorGUI.DrawRect(colorBarRect, entry.Color);

                // 内容
                var contentRect = new Rect(popupRect.x + 10, popupRect.y + 5, popupWidth - 20, popupHeight - 10);
                var oldColor = GUI.color;
                GUI.color = Color.white;
                EditorGUI.LabelField(contentRect, fullText, _infoBoxStyle);
                GUI.color = oldColor;

                // 关闭按钮
                var closeRect = new Rect(popupRect.xMax - 22, popupRect.y + 3, 18, 18);
                if (GUI.Button(closeRect, "×", EditorStyles.miniButton))
                {
                    ShowInfoPopup = false;
                }
            }
            Handles.EndGUI();
        }

        /// <summary>
        /// 计算标签的世界空间位置（在对象包围盒上方）
        /// </summary>
        private static Vector3 GetLabelWorldPosition(Vector3 position, Bounds bounds)
        {
            float offsetY = bounds.extents.y + 0.5f;
            return position + Vector3.up * offsetY;
        }

        /// <summary>
        /// 绘制包围盒线框
        /// </summary>
        private static void DrawWireBounds(Bounds bounds, float thickness)
        {
            var min = bounds.min;
            var max = bounds.max;

            // 底面
            Vector3 b0 = new Vector3(min.x, min.y, min.z);
            Vector3 b1 = new Vector3(max.x, min.y, min.z);
            Vector3 b2 = new Vector3(max.x, min.y, max.z);
            Vector3 b3 = new Vector3(min.x, min.y, max.z);

            // 顶面
            Vector3 t0 = new Vector3(min.x, max.y, min.z);
            Vector3 t1 = new Vector3(max.x, max.y, min.z);
            Vector3 t2 = new Vector3(max.x, max.y, max.z);
            Vector3 t3 = new Vector3(min.x, max.y, max.z);

            // 底面边
            DrawThickLine(b0, b1, thickness);
            DrawThickLine(b1, b2, thickness);
            DrawThickLine(b2, b3, thickness);
            DrawThickLine(b3, b0, thickness);

            // 顶面边
            DrawThickLine(t0, t1, thickness);
            DrawThickLine(t1, t2, thickness);
            DrawThickLine(t2, t3, thickness);
            DrawThickLine(t3, t0, thickness);

            // 竖直边
            DrawThickLine(b0, t0, thickness);
            DrawThickLine(b1, t1, thickness);
            DrawThickLine(b2, t2, thickness);
            DrawThickLine(b3, t3, thickness);
        }

        /// <summary>
        /// 绘制指定粗细的线段
        /// </summary>
        private static void DrawThickLine(Vector3 a, Vector3 b, float thickness)
        {
            Handles.DrawAAPolyLine(thickness, a, b);
        }

        /// <summary>
        /// 聚焦到指定条目
        /// </summary>
        public static void FocusOnEntry(int entryId)
        {
            if (!GizmosDebugRegistry.TryGetEntry(entryId, out var entry))
                return;

            if (entry.Target == null || !entry.Target.IsAlive)
                return;

            SelectedEntryId = entryId;
            ShowInfoPopup = true;

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                var bounds = entry.Target.DebugBounds;
                float size = Mathf.Max(bounds.extents.magnitude * 2f, 3f);
                sceneView.LookAt(entry.Target.DebugWorldPosition, sceneView.rotation, size);
            }

            // 在Hierarchy中选中关联的GameObject
            if (entry.Target is Component comp && comp != null)
            {
                Selection.activeGameObject = comp.gameObject;
            }

            OnSelectionChanged?.Invoke(entryId);
        }
    }
}
