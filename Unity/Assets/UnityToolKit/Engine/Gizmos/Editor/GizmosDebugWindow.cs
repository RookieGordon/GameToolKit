/*
 * 功能描述：Gizmos调试可视化控制面板（EditorWindow）
 *           提供全局/单项开关、搜索过滤、定位聚焦、详情查看等功能
 */

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityToolKit.Engine.Gizmos;

namespace UnityToolKit.Editor.Gizmos
{
    /// <summary>
    /// Gizmos调试可视化控制面板
    /// </summary>
    public class GizmosDebugWindow : EditorWindow
    {
        #region 常量

        private const string WindowTitle = "Gizmos Debug";
        private const string MenuPath = "Tools/ToolKit/Gizmos Debug Visualizer";
        private const float MinWidth = 320f;
        private const float MinHeight = 400f;

        #endregion

        #region 状态

        private Vector2 _scrollPosition;
        private string _searchFilter = "";
        private bool _showIntangibleOnly = false;
        private bool _showTangibleOnly = false;
        private int _selectedEntryId = -1;
        private bool _showSettings = true;
        private bool _showEntryList = true;
        private bool _showDetailPanel = false;

        // 缓存
        private List<GizmosDebugEntry> _filteredEntries = new List<GizmosDebugEntry>();
        private bool _entriesDirty = true;

        #endregion

        #region Styles

        private GUIStyle _headerStyle;
        private GUIStyle _entryStyle;
        private GUIStyle _entrySelectedStyle;
        private GUIStyle _statsStyle;
        private GUIStyle _detailStyle;
        private GUIStyle _tagTangibleStyle;
        private GUIStyle _tagIntangibleStyle;
        private bool _stylesInitialized;

        #endregion

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            var window = GetWindow<GizmosDebugWindow>(false, WindowTitle);
            window.minSize = new Vector2(MinWidth, MinHeight);
            window.Show();
        }

        private void OnEnable()
        {
            GizmosDebugRegistry.OnRegistryChanged += OnRegistryChanged;
            GizmosDebugSceneView.OnSelectionChanged += OnSceneSelectionChanged;
            GizmosDebugSceneView.Enable();
            _entriesDirty = true;
        }

        private void OnDisable()
        {
            GizmosDebugRegistry.OnRegistryChanged -= OnRegistryChanged;
            GizmosDebugSceneView.OnSelectionChanged -= OnSceneSelectionChanged;
        }

        private void OnRegistryChanged()
        {
            _entriesDirty = true;
            Repaint();
        }

        private void OnSceneSelectionChanged(int entryId)
        {
            _selectedEntryId = entryId;
            _showDetailPanel = true;
            Repaint();
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                margin = new RectOffset(4, 4, 4, 4),
            };

            _entryStyle = new GUIStyle("helpBox")
            {
                padding = new RectOffset(8, 8, 6, 6),
                margin = new RectOffset(4, 4, 2, 2),
            };

            _entrySelectedStyle = new GUIStyle(_entryStyle);

            _statsStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
            };

            _detailStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 8, 8),
                richText = true,
                wordWrap = true,
                fontSize = 11,
            };

            _tagTangibleStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.5f, 0.9f, 0.5f) },
                fontStyle = FontStyle.Bold,
            };

            _tagIntangibleStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(1f, 0.7f, 0.3f) },
                fontStyle = FontStyle.Bold,
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitStyles();

            DrawToolbar();
            DrawGlobalSettings();
            DrawSearchBar();
            DrawEntryList();
            DrawDetailPanel();
            DrawFooter();
        }

        #region Toolbar

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            {
                // 全局开关
                var icon = GizmosDebugRegistry.GlobalEnabled ? "d_VisibilityOn" : "d_VisibilityOff";
                var toggleContent = EditorGUIUtility.IconContent(icon);
                toggleContent.tooltip = GizmosDebugRegistry.GlobalEnabled ? "点击关闭全局显示" : "点击开启全局显示";

                if (GUILayout.Button(toggleContent, EditorStyles.toolbarButton, GUILayout.Width(30)))
                {
                    GizmosDebugRegistry.GlobalEnabled = !GizmosDebugRegistry.GlobalEnabled;
                    SceneView.RepaintAll();
                }

                // 标题
                GUILayout.Label(WindowTitle, EditorStyles.toolbarButton);

                GUILayout.FlexibleSpace();

                // 清理失效条目
                if (GUILayout.Button(new GUIContent("清理", "移除已失效的条目"), EditorStyles.toolbarButton, GUILayout.Width(40)))
                {
                    GizmosDebugRegistry.Cleanup();
                }

                // 全部清除
                if (GUILayout.Button(new GUIContent("重置", "清除所有注册条目"), EditorStyles.toolbarButton, GUILayout.Width(40)))
                {
                    if (EditorUtility.DisplayDialog("确认重置", "这将清除所有已注册的调试目标，确定执行？", "确定", "取消"))
                    {
                        GizmosDebugRegistry.Clear();
                        _selectedEntryId = -1;
                        GizmosDebugSceneView.SelectedEntryId = -1;
                        GizmosDebugSceneView.ShowInfoPopup = false;
                    }
                }

                // 刷新
                var refreshContent = EditorGUIUtility.IconContent("d_Refresh");
                refreshContent.tooltip = "刷新列表";
                if (GUILayout.Button(refreshContent, EditorStyles.toolbarButton, GUILayout.Width(30)))
                {
                    _entriesDirty = true;
                    SceneView.RepaintAll();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region 全局设置

        private void DrawGlobalSettings()
        {
            _showSettings = EditorGUILayout.Foldout(_showSettings, "显示设置", true);
            if (!_showSettings) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.BeginVertical("box");
            {
                // 标签字体大小
                var newFontSize = EditorGUILayout.IntSlider("标签字体大小", GizmosDebugRegistry.LabelFontSize, 8, 24);
                if (newFontSize != GizmosDebugRegistry.LabelFontSize)
                {
                    GizmosDebugRegistry.LabelFontSize = newFontSize;
                    _stylesInitialized = false; // 需要重建样式
                    SceneView.RepaintAll();
                }

                // Gizmo尺寸
                var newGizmoSize = EditorGUILayout.Slider("无形对象Gizmo大小", GizmosDebugRegistry.GizmoSize, 0.1f, 3f);
                if (!Mathf.Approximately(newGizmoSize, GizmosDebugRegistry.GizmoSize))
                {
                    GizmosDebugRegistry.GizmoSize = newGizmoSize;
                    SceneView.RepaintAll();
                }

                // 显示包围盒
                var newShowBounds = EditorGUILayout.Toggle("显示包围盒线框", GizmosDebugRegistry.ShowBounds);
                if (newShowBounds != GizmosDebugRegistry.ShowBounds)
                {
                    GizmosDebugRegistry.ShowBounds = newShowBounds;
                    SceneView.RepaintAll();
                }

                // 显示连接线
                var newShowConnectors = EditorGUILayout.Toggle("显示标签连接线", GizmosDebugRegistry.ShowConnectors);
                if (newShowConnectors != GizmosDebugRegistry.ShowConnectors)
                {
                    GizmosDebugRegistry.ShowConnectors = newShowConnectors;
                    SceneView.RepaintAll();
                }
            }
            EditorGUILayout.EndVertical();
            EditorGUI.indentLevel--;
        }

        #endregion

        #region 搜索过滤

        private void DrawSearchBar()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            {
                // 搜索栏
                var newSearch = EditorGUILayout.TextField(
                    GUIContent.none,
                    _searchFilter,
                    EditorStyles.toolbarSearchField
                );
                if (newSearch != _searchFilter)
                {
                    _searchFilter = newSearch;
                    _entriesDirty = true;
                }

                // 过滤按钮（使用GUILayout.Toggle模拟工具栏按钮样式）
                var newTangibleOnly = GUILayout.Toggle(_showTangibleOnly, new GUIContent("有形", "仅显示有形对象"), EditorStyles.toolbarButton, GUILayout.Width(36));
                if (newTangibleOnly != _showTangibleOnly)
                {
                    _showTangibleOnly = newTangibleOnly;
                    if (_showTangibleOnly) _showIntangibleOnly = false;
                    _entriesDirty = true;
                }

                var newIntangibleOnly = GUILayout.Toggle(_showIntangibleOnly, new GUIContent("无形", "仅显示无形对象"), EditorStyles.toolbarButton, GUILayout.Width(36));
                if (newIntangibleOnly != _showIntangibleOnly)
                {
                    _showIntangibleOnly = newIntangibleOnly;
                    if (_showIntangibleOnly) _showTangibleOnly = false;
                    _entriesDirty = true;
                }
            }
            EditorGUILayout.EndHorizontal();

            // 全选/全不选
            EditorGUILayout.BeginHorizontal();
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("全选", EditorStyles.miniButtonLeft, GUILayout.Width(40)))
                {
                    GizmosDebugRegistry.SetAllEnabled(true);
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button("全不选", EditorStyles.miniButtonRight, GUILayout.Width(50)))
                {
                    GizmosDebugRegistry.SetAllEnabled(false);
                    SceneView.RepaintAll();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region 条目列表

        private void UpdateFilteredEntries()
        {
            if (!_entriesDirty) return;
            _entriesDirty = false;

            _filteredEntries.Clear();
            foreach (var kvp in GizmosDebugRegistry.Entries)
            {
                var entry = kvp.Value;
                if (entry.Target == null || !entry.Target.IsAlive)
                    continue;

                // 搜索过滤
                if (!string.IsNullOrEmpty(_searchFilter))
                {
                    string name = entry.Target.DebugDisplayName ?? "";
                    if (!name.ToLower().Contains(_searchFilter.ToLower()))
                        continue;
                }

                // 类型过滤
                if (_showTangibleOnly && !entry.Target.IsTangible) continue;
                if (_showIntangibleOnly && entry.Target.IsTangible) continue;

                _filteredEntries.Add(entry);
            }

            _filteredEntries = _filteredEntries.OrderBy(e => e.Id).ToList();
        }

        private void DrawEntryList()
        {
            UpdateFilteredEntries();

            EditorGUILayout.Space(4);
            _showEntryList = EditorGUILayout.Foldout(_showEntryList, $"调试目标 ({_filteredEntries.Count})", true);
            if (!_showEntryList) return;

            if (_filteredEntries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    GizmosDebugRegistry.Count == 0
                        ? "暂无已注册的调试目标。\n将 GizmosTargetBase 组件挂载到GameObject上，\n或在代码中实现 IGizmosDebugTarget 接口并调用 GizmosDebugRegistry.Register()。"
                        : "没有符合过滤条件的条目。",
                    MessageType.Info);
                return;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            {
                foreach (var entry in _filteredEntries)
                {
                    DrawEntryItem(entry);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawEntryItem(GizmosDebugEntry entry)
        {
            bool isSelected = entry.Id == _selectedEntryId;
            var target = entry.Target;

            // 背景色
            var bgColor = isSelected ? new Color(entry.Color.r, entry.Color.g, entry.Color.b, 0.2f) : Color.clear;

            var rect = EditorGUILayout.BeginVertical(_entryStyle);
            {
                if (isSelected)
                    EditorGUI.DrawRect(rect, bgColor);

                EditorGUILayout.BeginHorizontal();
                {
                    // 启用开关
                    var newEnabled = EditorGUILayout.Toggle(GUIContent.none, entry.Enabled, GUILayout.Width(18));
                    if (newEnabled != entry.Enabled)
                    {
                        GizmosDebugRegistry.SetEnabled(entry.Id, newEnabled);
                        SceneView.RepaintAll();
                    }

                    // 颜色指示条
                    var colorRect = GUILayoutUtility.GetRect(6, 20, GUILayout.Width(6));
                    EditorGUI.DrawRect(colorRect, entry.Color);

                    // 编号
                    GUILayout.Label($"#{entry.Id:D2}", EditorStyles.miniLabel, GUILayout.Width(28));

                    // 名称 + 可点击选中
                    var nameStyle = isSelected ? EditorStyles.boldLabel : EditorStyles.label;
                    if (GUILayout.Button(target.DebugDisplayName, nameStyle))
                    {
                        SelectEntry(entry.Id);
                    }

                    // 类型标签
                    var typeStyle = target.IsTangible ? _tagTangibleStyle : _tagIntangibleStyle;
                    string typeText = target.IsTangible ? "[有形]" : "[无形]";
                    GUILayout.Label(typeText, typeStyle, GUILayout.Width(42));
                }
                EditorGUILayout.EndHorizontal();

                // 第二行：操作按钮
                EditorGUILayout.BeginHorizontal();
                {
                    GUILayout.Space(26); // 与上面对齐

                    // 颜色选择
                    var newColor = EditorGUILayout.ColorField(GUIContent.none, entry.Color, false, false, false, GUILayout.Width(30));
                    if (newColor != entry.Color)
                    {
                        GizmosDebugRegistry.SetColor(entry.Id, newColor);
                        SceneView.RepaintAll();
                    }

                    // 位置信息
                    string posText = $"({target.DebugWorldPosition.x:F1}, {target.DebugWorldPosition.y:F1}, {target.DebugWorldPosition.z:F1})";
                    GUILayout.Label(posText, EditorStyles.miniLabel);

                    GUILayout.FlexibleSpace();

                    // 定位按钮
                    var focusContent = EditorGUIUtility.IconContent("d_SceneViewCamera");
                    focusContent.tooltip = "在Scene视图中聚焦此目标";
                    if (GUILayout.Button(focusContent, EditorStyles.miniButton, GUILayout.Width(28), GUILayout.Height(18)))
                    {
                        GizmosDebugSceneView.FocusOnEntry(entry.Id);
                        _selectedEntryId = entry.Id;
                        _showDetailPanel = true;
                    }

                    // Ping按钮（关联GameObject时生效）
                    if (target is Component comp && comp != null)
                    {
                        var pingContent = EditorGUIUtility.IconContent("d_Linked");
                        pingContent.tooltip = "在Hierarchy中Ping此对象";
                        if (GUILayout.Button(pingContent, EditorStyles.miniButton, GUILayout.Width(28), GUILayout.Height(18)))
                        {
                            EditorGUIUtility.PingObject(comp.gameObject);
                            Selection.activeGameObject = comp.gameObject;
                        }
                    }

                    // 信息按钮
                    var infoContent = EditorGUIUtility.IconContent("d__Help");
                    infoContent.tooltip = "查看详细信息";
                    if (GUILayout.Button(infoContent, EditorStyles.miniButton, GUILayout.Width(28), GUILayout.Height(18)))
                    {
                        SelectEntry(entry.Id);
                        _showDetailPanel = true;
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        private void SelectEntry(int entryId)
        {
            _selectedEntryId = entryId;
            GizmosDebugSceneView.SelectedEntryId = entryId;
            _showDetailPanel = true;

            if (GizmosDebugRegistry.TryGetEntry(entryId, out var entry))
            {
                if (entry.Target is Component comp && comp != null)
                {
                    Selection.activeGameObject = comp.gameObject;
                }
            }

            SceneView.RepaintAll();
            Repaint();
        }

        #endregion

        #region 详情面板

        private void DrawDetailPanel()
        {
            if (!_showDetailPanel || _selectedEntryId < 0)
                return;

            if (!GizmosDebugRegistry.TryGetEntry(_selectedEntryId, out var entry))
            {
                _showDetailPanel = false;
                return;
            }

            if (entry.Target == null || !entry.Target.IsAlive)
            {
                _showDetailPanel = false;
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.LabelField($"详情 - [{entry.Id:D2}] {entry.Target.DebugDisplayName}", _headerStyle);
                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20)))
                {
                    _showDetailPanel = false;
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginVertical("box");
            {
                // 类型
                string typeStr = entry.Target.IsTangible ? "有形对象（具有Renderer）" : "无形对象（无Renderer，支持交互）";
                EditorGUILayout.LabelField("类型", typeStr);

                // 位置
                EditorGUILayout.Vector3Field("世界位置", entry.Target.DebugWorldPosition);

                // 包围盒
                var bounds = entry.Target.DebugBounds;
                EditorGUILayout.LabelField("包围盒中心", bounds.center.ToString("F2"));
                EditorGUILayout.LabelField("包围盒尺寸", bounds.size.ToString("F2"));

                EditorGUILayout.Space(4);

                // 详细信息
                EditorGUILayout.LabelField("详细信息", EditorStyles.boldLabel);
                string info = entry.Target.DebugDetailInfo ?? "无";
                EditorGUILayout.LabelField(info, _detailStyle, GUILayout.MinHeight(40));

                EditorGUILayout.Space(4);

                // 交互按钮
                EditorGUILayout.BeginHorizontal();
                {
                    if (GUILayout.Button("触发交互", GUILayout.Height(24)))
                    {
                        entry.Target.OnDebugInteract();
                    }

                    if (GUILayout.Button("聚焦Scene", GUILayout.Height(24)))
                    {
                        GizmosDebugSceneView.FocusOnEntry(entry.Id);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        #endregion

        #region Footer

        private void DrawFooter()
        {
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginHorizontal();
            {
                // 统计
                int total = GizmosDebugRegistry.Count;
                int tangible = 0, intangible = 0, enabled = 0;
                foreach (var kvp in GizmosDebugRegistry.Entries)
                {
                    if (kvp.Value.Target != null && kvp.Value.Target.IsAlive)
                    {
                        if (kvp.Value.Target.IsTangible) tangible++;
                        else intangible++;
                        if (kvp.Value.Enabled) enabled++;
                    }
                }

                GUILayout.Label(
                    $"总计: {total}  |  有形: {tangible}  |  无形: {intangible}  |  启用: {enabled}",
                    _statsStyle
                );
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);
        }

        #endregion

        /// <summary>
        /// 定期刷新（即使窗口不活跃也需要跟踪状态变化）
        /// </summary>
        private void Update()
        {
            // 同步Scene视图的选中状态
            if (GizmosDebugSceneView.SelectedEntryId != _selectedEntryId)
            {
                _selectedEntryId = GizmosDebugSceneView.SelectedEntryId;
                Repaint();
            }
        }
    }
}
