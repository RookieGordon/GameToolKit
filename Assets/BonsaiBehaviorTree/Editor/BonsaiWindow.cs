using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bonsai.Core;
using Bonsai.Utility;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace Bonsai.Designer
{
    public class BonsaiWindow : EditorWindow
    {
        [MenuItem("Window/Bonsai Designer")]
        static void Init()
        {
            var window = CreateInstance<BonsaiWindow>();
            window.titleContent = new GUIContent("Bonsai");
            window.minSize = new Vector2(500, 500);
            window.Show();
        }

        public const float ToolbarHeight = 30;

        // We serialize the reference to the opened tree.
        // This way, when a editor window is left opened and Unity closes,
        // the tree opens up with the editor window.
        [SerializeField] private BehaviourTreeProxy _treeProxy;

        public BehaviourTree Tree
        {
            get
            {
                if (this._treeProxy != null)
                {
                    return this._treeProxy.Tree;
                }

                return null;
            }
        }

        private BonsaiEditor Editor { get; set; }

        public IReadOnlyList<BonsaiNode> Nodes => Editor.Canvas.Nodes;

        public BonsaiViewer Viewer { get; private set; }
        public BonsaiSaver Saver { get; private set; }

        // The editor state without needing a reference to the Editor instance.
        // This is used to solve initialization order issues for OnEnable.
        // This allows Inspectors to view the editor mode if they were enabled before the window.
        public BonsaiEditor.Mode EditorMode { get; private set; }

        private bool _isDebugging = false;
        private int _breakPoint = -1;

        void OnEnable()
        {
            BonsaiPreferences.Instance = BonsaiPreferences.LoadDefaultPreferences();
            BonsaiEditor.FetchBehaviourNodes();

            Editor = new BonsaiEditor();
            Viewer = new BonsaiViewer();
            Saver = new BonsaiSaver();

            Saver.SaveMessage += (sender, message) => ShowNotification(new GUIContent(message));
            Saver.OnSaveDone += (snder, success) => AfterSaveTree();

            Editor.Viewer = Viewer;
            Editor.Input.SaveRequest += (s, e) => Save();
            Editor.CanvasChanged += (s, e) => Repaint();
            Editor.Input.MouseDown += (s, e) => Repaint();
            Editor.Input.MouseUp += (s, e) => Repaint();
            Editor.EditorMode.ValueChanged += (s, mode) => { EditorMode = mode; };

            BehaviourTree.AfterInit += AfterTreeInitedInPlaying;

            EditorApplication.playModeStateChanged += PlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            Selection.selectionChanged += SelectionChanged;

            BuildCanvas();
            Editor.EditorMode.Value = BonsaiEditor.Mode.Edit;
            SwitchToViewModeIfRequired();

            this.InitDebugBtnReoueces();
            this.InitToolBarStyle();
        }

        void OnDisable()
        {
            EditorApplication.playModeStateChanged -= PlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
            BehaviourTree.AfterInit -= AfterTreeInitedInPlaying;
            Selection.selectionChanged -= SelectionChanged;
        }

        void OnDestroy()
        {
            OnExit();
        }

        void OnGUI()
        {
            if (Tree == null)
            {
                Viewer.DrawStaticGrid(position.size);
                Viewer.DrawMode();
                Editor.EditorMode.Value = BonsaiEditor.Mode.Edit;
            }

            else
            {
                // Make sure to build a canvas for an active tree.
                if (Editor.Canvas == null)
                {
                    BuildCanvas();
                }

                CanvasTransform t = Transform;
                Editor.PollInput(Event.current, t, CanvasInputRect);
                Viewer.Draw(t);
            }

            DrawToolbar();
            UpdateWindowTitle();
        }

        void Update()
        {
            // Update the window during the play mode when the window
            // is viewing a tree instance of a game object.
            // This is to quicky update all changes of the tree.
            bool bConditions =
                Editor.EditorMode.Value == BonsaiEditor.Mode.View &&
                EditorApplication.isPlaying &&
                Tree != null &&
                Tree.IsRunning();

            if (bConditions)
            {
                Repaint();
            }
        }

        private void BeforeAssemblyReload()
        {
            // Do not attempt to do saving if about to enter play mode since that is handled in PlayModeStateChanged.
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                // OnExit();
                this.Close();
            }
        }

        private void PlayModeStateChanged(PlayModeStateChange state)
        {
            // Before entering play mode, attempt to save the current tree asset. 
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                QuickSave();
                this._treeProxy = null;
            }

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                SwitchToViewModeIfRequired();
            }

            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                this._treeProxy = null;
            }
        }

        private void SelectionChanged()
        {
            SwitchToViewModeIfRequired();
        }

        /// <summary>
        /// Updates the GUI contents for each node that is currently selected.
        /// </summary>
        public void UpdateSelectedNodesGUI()
        {
            Editor.UpdateNodesGUI(Editor.NodeSelection.SelectedNodes);
            Repaint();
        }

        /// <summary>
        /// Updates the GUI contents for the node.
        /// </summary>
        /// <param name="behaviour">The associated visual node will be update for this behaviour.</param>
        public void UpdateNodeGUI(BehaviourNode behaviour)
        {
            Editor.UpdateNodeGUI(behaviour);
            Repaint();
        }

        public bool ContainsNode(BehaviourNode behaviour)
        {
            var list = Editor.Canvas.Nodes.Select(n => n.Behaviour);
            return list.Contains(behaviour);
        }

        private void SwitchToViewModeIfRequired()
        {
            // Cannot go to view mode.
            if (!EditorApplication.isPlaying || !Selection.activeGameObject)
            {
                return;
            }

            var btc = Selection.activeGameObject.GetComponent<BonsaiTreeComponent>();
            BehaviourTree treeToView = btc ? btc.Tree : null;

            // There must be a non-null tree to view,
            // it must be a different tree than the active tree for this window,
            // and must not be opened somewhere else.
            if (treeToView != null && Tree != treeToView)
            {
                var windows = Resources.FindObjectsOfTypeAll<BonsaiWindow>();

                bool alreadyInView = windows.Any(w => w.Tree == treeToView);

                if (alreadyInView)
                {
                    return;
                }

                BonsaiWindow window = windows.FirstOrDefault(w => w.Tree == null);

                // Have the window without a set tree to view the tree selected.
                if (window)
                {
                    window.SetTree(treeToView, BonsaiEditor.Mode.View);
                }
                else
                {
                    // View tree in this window.
                    SetTree(treeToView, BonsaiEditor.Mode.View);
                }
            }
        }

        private void BuildCanvas()
        {
            if (Tree != null)
            {
                Editor.SetBehaviourTree(Tree);
                Repaint();
            }
        }

        private void NicifyTree()
        {
            if (Tree != null && Editor.Canvas != null)
            {
                if (Editor.Canvas.Root == null)
                {
                    ShowNotification(new GUIContent("Set a root to nicely format the tree!"));
                }
                else
                {
                    Formatter.PositionNodesNicely(Editor.Canvas.Root, Vector2.zero);
                }
            }
        }

        public void SetTree(BehaviourTree bt, BonsaiEditor.Mode mode = BonsaiEditor.Mode.Edit)
        {
            this._treeProxy = bt.Proxy;
            BuildCanvas();
            Editor.EditorMode.Value = mode;
        }

        private GUIStyle _toolBarStyle;
        private GUIStyle _toolbarDropDownStyle;
        private GUIStyle _labelStyle;

        private Texture2D _debugIcon;
        private Texture2D _stopIcon;
        private Texture2D _stepOverIcon;
        private Texture2D _resumeIcon;

        private void InitDebugBtnReoueces()
        {
            this._debugIcon = Resources.Load("Debug") as Texture2D;
            this._stopIcon = Resources.Load("Stop") as Texture2D;
            this._stepOverIcon = Resources.Load("StepOver") as Texture2D;
            this._resumeIcon = Resources.Load("Resume") as Texture2D;
        }

        private void InitToolBarStyle()
        {
            this._toolBarStyle = new GUIStyle { name = "Toolbar", fixedHeight = 30, };
            this._labelStyle = new GUIStyle { alignment = TextAnchor.MiddleCenter, fixedHeight = 30, };
            this._labelStyle.normal.textColor = Color.white;
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(this._toolBarStyle);

            var dropDownStyle = EditorStyles.toolbarDropDown;
            dropDownStyle.fixedHeight = 30f;
            if (GUILayout.Button("File", dropDownStyle, GUILayout.Width(50f)))
            {
                if (Editor.EditorMode.Value == BonsaiEditor.Mode.Edit)
                {
                    CreateFileMenuEditable();
                }
                else
                {
                    CreateFileMenuViewOnly();
                }
            }

            if (GUILayout.Button("View", dropDownStyle, GUILayout.Width(50f)))
            {
                var fileMenu = new GenericMenu();
                fileMenu.AddItem(new GUIContent("Home Zoom"), false, HomeZoom);
                fileMenu.DropDown(new Rect(55f, ToolbarHeight, 0f, 0f));
            }

            if (GUILayout.Button("Tools", dropDownStyle, GUILayout.Width(50f)))
            {
                var fileMenu = new GenericMenu();
                fileMenu.AddItem(new GUIContent("Nicefy Tree"), false, NicifyTree);
                fileMenu.AddItem(new GUIContent("Refresh Editor"), false, RefreshEditor);
                fileMenu.DropDown(new Rect(105f, ToolbarHeight, 0f, 0f));
            }

            GUILayout.FlexibleSpace();

            this.DrawDebug();
            GUILayout.Label(TreeName(), this._labelStyle);

            EditorGUILayout.EndHorizontal();
        }

        private void DrawDebug()
        {
            var image = this._isDebugging ? this._stopIcon : this._debugIcon;
            if (GUILayout.Button(image, GUILayout.Width(30f), GUILayout.Height(28f)))
            {
                this._isDebugging = !this._isDebugging;
                if (!this._isDebugging)
                {
                    this.StopDebug();
                }
            }

            if (this._isDebugging)
            {
                GUILayout.Label("断点：", this._labelStyle);
                var style = this._labelStyle;
                var point = EditorGUILayout.IntField(this._breakPoint, this._labelStyle, GUILayout.Width(40f));
                if (point != this._breakPoint)
                {
                    this._breakPoint = point;
                    this.SetBreakPoint(point);
                }

                if (GUILayout.Button(this._resumeIcon, GUILayout.Width(30f), GUILayout.Height(28f)))
                {
                    this.Resume();
                }

                if (GUILayout.Button(this._stepOverIcon, GUILayout.Width(30f), GUILayout.Height(28f)))
                {
                    this.StepOver();
                }
            }
        }

        private string TreeName()
        {
            return Tree != null
                ? (Tree.name.Length == 0 ? "New Tree" : Tree.name)
                : "None";
        }

        private void UpdateWindowTitle()
        {
            if (Tree != null && Tree.name.Length != 0)
            {
                if (titleContent.text != Tree.name)
                {
                    titleContent.text = Tree.name;
                }
            }
            else
            {
                titleContent.text = "Bonsai";
            }
        }

        private void CreateFileMenuEditable()
        {
            var fileMenu = new GenericMenu();

            fileMenu.AddItem(new GUIContent("Create New"), false, CreateNew);
            fileMenu.AddSeparator("");
            fileMenu.AddItem(new GUIContent("Load"), false, Load);
            fileMenu.AddItem(new GUIContent("Save"), false, Save);
            fileMenu.DropDown(new Rect(5f, ToolbarHeight, 0f, 0f));
        }

        private void CreateFileMenuViewOnly()
        {
            var fileMenu = new GenericMenu();

            fileMenu.AddDisabledItem(new GUIContent("Create New"));
            fileMenu.AddSeparator("");
            fileMenu.AddDisabledItem(new GUIContent("Load"));
            fileMenu.AddDisabledItem(new GUIContent("Save"));
            fileMenu.DropDown(new Rect(5f, ToolbarHeight, 0f, 0f));
        }

        // Centers and fits the entire tree in the view center.
        private void HomeZoom()
        {
            if (Tree == null) return;

            LogNotImplemented("Home Zoom");
        }

        private void RefreshEditor()
        {
            // Reload preferences.
            BonsaiPreferences.Instance = BonsaiPreferences.LoadDefaultPreferences();
            BuildCanvas();
        }

        /// <summary>
        /// create a new behaviour tree
        /// </summary>
        private void CreateNew()
        {
            QuickSave();
            SetTree(BonsaiSaver.CreateBehaviourTree());
            ShowNotification(new GUIContent("New Tree Created"));
        }

        private void Load()
        {
            // Save current canvas.
            QuickSave();

            BehaviourTree tree = Saver.LoadBehaviourTree();
            if (tree != null)
            {
                SetTree(tree);
            }
        }

        // Standard save procedure. Tree not in the AssetDatabase will prompt the user to select a save file.
        private void Save()
        {
            if (Editor.Canvas != null && EditorMode == BonsaiEditor.Mode.Edit)
            {
                Saver.SaveCanvas(Editor.Canvas, TreeMetaData);
            }
        }

        private void AfterSaveTree()
        {
            if (Editor.Canvas != null)
            {
                if (Editor.Canvas.Tree == null)
                {
                    Debug.Log("AfterSaveTree Editor.Canvas.Tree = null");
                }

                if (Editor.Canvas.Tree.Proxy == null)
                {
                    Debug.Log("AfterSaveTree Editor.Canvas.Tree.Proxy = null");
                }

                this._treeProxy = Editor.Canvas.Tree.Proxy;
            }
        }

        // A quick save only saves tree assets that already exist in the AssetDatabase.
        private void QuickSave()
        {
            if (EditorMode == BonsaiEditor.Mode.Edit && Saver.CanSaveTree(Tree))
            {
                Saver.SaveCanvas(Editor.Canvas, TreeMetaData);
            }
        }

        private void OnExit()
        {
            // This is to prevent active selection on objects that are no longer focused or do not exist after destroy.
            Editor.NodeSelection.ClearSelection();
            QuickSave();
            this._isDebugging = false;
            this._breakPoint = -1;
        }

        private CanvasTransform Transform
        {
            get
            {
                return new CanvasTransform
                {
                    pan = Viewer.panOffset,
                    zoom = Viewer.ZoomScale,
                    size = position.size
                };
            }
        }

        private BonsaiSaver.TreeMetaData TreeMetaData
        {
            get
            {
                return new BonsaiSaver.TreeMetaData
                {
                    zoom = Viewer.zoom,
                    pan = Viewer.panOffset
                };
            }
        }

        /// <summary>
        /// The rect used to filter input.
        /// This is so the toolbar is not ignored by editor inputs.
        /// </summary>
        public Rect CanvasInputRect
        {
            get
            {
                var rect = new Rect(Vector2.zero, position.size);
                rect.y += ToolbarHeight;
                rect.height -= ToolbarHeight;
                return rect;
            }
        }

        public static void LogNotImplemented(string msg)
        {
            Debug.Log("<color=maroon> Feature not implemented: " + msg + "</color>");
        }

        /// <summary>
        /// Opens the tree in a Bonsai Window.
        /// </summary>
        /// <param name="tree">The tree to open</param>
        /// <returns>
        /// The window that opens the tree. Null if already opened.
        /// </returns>
        public static BonsaiWindow OpenTree(BehaviourTree tree, BonsaiEditor.Mode mode = BonsaiEditor.Mode.Edit)
        {
            if (tree == null)
            {
                return null;
            }

            // Try to find an editor window without a canvas...
            var windows = Resources.FindObjectsOfTypeAll<BonsaiWindow>();

            bool isAlreadyOpened = windows.Any(w => w.Tree == tree);

            if (isAlreadyOpened)
            {
                return null;
            }

            // Find a window without any tree.
            BonsaiWindow window = windows.FirstOrDefault(w => w.Tree == null);

            // No windows available, make a new one.
            if (!window)
            {
                window = CreateInstance<BonsaiWindow>();
                window.Show();
            }

            window.SetTree(tree, mode);
            return window;
        }

        /// <summary>
        /// Opens up the Bonsai window from asset selection.
        /// </summary>
        /// <param name="instanceID"></param>
        /// <param name="line"></param>
        /// <returns></returns>
        [OnOpenAsset(0)]
        static bool OpenCanvasAsset(int instanceID, int line)
        {
            var treeProxy = EditorUtility.InstanceIDToObject(instanceID) as BehaviourTreeProxy;
            if (treeProxy == null)
            {
                return false;
            }

            treeProxy.InitBehaviourTree();
            treeProxy.ReConnectDataToNodeProxy();
            var tree = treeProxy.Tree;
            BonsaiWindow w = OpenTree(tree);
            if (w != null)
            {
                // If a tree asset was created but has no blackboard, add one upon opening. This is for convenience.
                BonsaiSaver.AddBlackboardIfMissing(tree);
                w.SwitchToViewModeIfRequired();
            }

            return w != null;
        }

        private void AfterTreeInitedInPlaying(BehaviourTree tree)
        {
            var treeProxy = EditorUtility.InstanceIDToObject(tree.AssetInstanceID) as BehaviourTreeProxy;
            if (treeProxy == null)
            {
                return;
            }

            treeProxy.BindTreeWithProxy(tree);
            if (tree != null)
            {
                SetTree(tree, BonsaiEditor.Mode.View);
                // BonsaiSaver.AddBlackboardIfMissing(tree);

                if (this._isDebugging)
                {
                    tree.Debugger = new TreeDebugger()
                        { BreakPoint = this._breakPoint, Running = true };
                }
            }
        }


        private void StopDebug()
        {
            if (Tree is { Debugger: { } })
            {
                Tree.Debugger.StopDebug();
            }
        }

        private void StepOver()
        {
            if (Tree is { Debugger: { } })
            {
                Tree.Debugger.StepOver = true;
            }
        }

        private void Resume()
        {
            if (Tree is { Debugger: { } })
            {
                Tree.Debugger.Resume = true;
            }
        }

        private void SetBreakPoint(int index)
        {
            if (Tree is { Debugger: { } })
            {
                Tree.Debugger.BreakPoint = index;
            }
        }
    }
}