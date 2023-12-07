// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.BehaviorDesignerWindow
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using ExternalBehavior = BehaviorDesigner.Runtime.ExternalBehavior;

namespace BehaviorDesigner.Editor
{
    public class BehaviorDesignerWindow : EditorWindow
    {
        [SerializeField] public static BehaviorDesignerWindow instance;
        private Rect mGraphRect;
        private Rect mGraphScrollRect;
        private Rect mFileToolBarRect;
        private Rect mDebugToolBarRect;
        private Rect mPropertyToolbarRect;
        private Rect mPropertyBoxRect;
        private Rect mPreferencesPaneRect;
        private Rect mFindDialogueRect;
        private Rect mQuickTaskListRect;
        private Vector2 mGraphScrollSize = new Vector2(20000f, 20000f);
        private bool mSizesInitialized;
        private float mPrevScreenWidth = -1f;
        private float mPrevScreenHeight = -1f;
        private bool mPropertiesPanelOnLeft = true;
        private Vector2 mCurrentMousePosition = Vector2.zero;
        private Vector2 mGraphScrollPosition = new Vector2(-1f, -1f);
        private Vector2 mGraphOffset = Vector2.zero;
        private float mGraphZoom = 1f;
        private float mGraphZoomMultiplier = 1f;
        private int mBehaviorToolbarSelection = 2;

        private string[] mBehaviorToolbarStrings = new string[4]
        {
            "Behavior",
            "Tasks",
            "Variables",
            "Inspector"
        };

        private string mGraphStatus = string.Empty;
        private Material mGridMaterial;
        private Vector2 mSelectStartPosition = Vector2.zero;
        private Rect mSelectionArea;
        private bool mIsSelecting;
        private bool mIsDragging;
        private bool mKeepTasksSelected;
        private bool mNodeClicked;
        private Vector2 mDragDelta = Vector2.zero;
        private bool mCommandDown;
        private bool mUpdateNodeTaskMap;
        private bool mStepApplication;
        private Dictionary<NodeDesigner, Task> mNodeDesignerTaskMap;
        private bool mEditorAtBreakpoint;
        [SerializeField] private List<BehaviorDesigner.Editor.ErrorDetails> mErrorDetails;
        private bool mShowFindDialogue;
        private string mFindTaskValue;
        private SharedVariable mFindSharedVariable;
        private bool mShowQuickTaskList;
        private GenericMenu mRightClickMenu;
        [SerializeField] private GenericMenu mBreadcrumbGameObjectBehaviorMenu;
        [SerializeField] private GenericMenu mBreadcrumbGameObjectMenu;
        [SerializeField] private GenericMenu mBreadcrumbBehaviorMenu;
        [SerializeField] private GenericMenu mReferencedBehaviorsMenu;
        private bool mShowRightClickMenu;
        private bool mShowPrefPane;
        [SerializeField] private GraphDesigner mGraphDesigner;
        private TaskInspector mTaskInspector;
        private TaskList mTaskList;
        private VariableInspector mVariableInspector;
        [SerializeField] private UnityEngine.Object mActiveObject;
        private UnityEngine.Object mPrevActiveObject;
        private BehaviorSource mActiveBehaviorSource;
        private BehaviorSource mExternalParent;
        private int mActiveBehaviorID = -1;
        [SerializeField] private List<UnityEngine.Object> mBehaviorSourceHistory = new List<UnityEngine.Object>();
        [SerializeField] private int mBehaviorSourceHistoryIndex = -1;
        private BehaviorManager mBehaviorManager;
        private bool mLockActiveGameObject;
        private bool mLoadedFromInspector;
        [SerializeField] private bool mIsPlaying;
        private UnityWebRequest mUpdateCheckRequest;
        private DateTime mLastUpdateCheck = DateTime.MinValue;
        private string mLatestVersion;
        private bool mTakingScreenshot;
        private float mScreenshotStartGraphZoom;
        private Vector2 mScreenshotStartGraphOffset;
        private Texture2D mScreenshotTexture;
        private Rect mScreenshotGraphSize;
        private Vector2 mScreenshotGraphOffset;
        private string mScreenshotPath;
        public BehaviorDesignerWindow.TaskCallbackHandler onAddTask;
        public BehaviorDesignerWindow.TaskCallbackHandler onRemoveTask;
        private List<TaskSerializer> mCopiedTasks;

        public List<BehaviorDesigner.Editor.ErrorDetails> ErrorDetails => this.mErrorDetails;

        public TaskList TaskList => this.mTaskList;

        public BehaviorSource ActiveBehaviorSource => this.mActiveBehaviorSource;

        public int ActiveBehaviorID => this.mActiveBehaviorID;

        private DateTime LastUpdateCheck
        {
            get
            {
                try
                {
                    if (this.mLastUpdateCheck != DateTime.MinValue)
                        return this.mLastUpdateCheck;
                    this.mLastUpdateCheck = DateTime.Parse(EditorPrefs.GetString("BehaviorDesignerLastUpdateCheck", "1/1/1971 00:00:01"), (IFormatProvider)CultureInfo.InvariantCulture);
                }
                catch (Exception ex)
                {
                    this.mLastUpdateCheck = DateTime.UtcNow;
                }

                return this.mLastUpdateCheck;
            }
            set
            {
                this.mLastUpdateCheck = value;
                EditorPrefs.SetString("BehaviorDesignerLastUpdateCheck", this.mLastUpdateCheck.ToString((IFormatProvider)CultureInfo.InvariantCulture));
            }
        }

        public string LatestVersion
        {
            get
            {
                if (!string.IsNullOrEmpty(this.mLatestVersion))
                    return this.mLatestVersion;
                this.mLatestVersion = EditorPrefs.GetString("BehaviorDesignerLatestVersion", "1.7.7".ToString());
                return this.mLatestVersion;
            }
            set
            {
                this.mLatestVersion = value;
                EditorPrefs.SetString("BehaviorDesignerLatestVersion", this.mLatestVersion);
            }
        }

        public BehaviorDesignerWindow.TaskCallbackHandler OnAddTask
        {
            get => this.onAddTask;
            set => this.onAddTask += value;
        }

        public BehaviorDesignerWindow.TaskCallbackHandler OnRemoveTask
        {
            get => this.onRemoveTask;
            set => this.onRemoveTask += value;
        }

        [MenuItem("Tools/Behavior Designer/Editor", false, 0)]
        public static void ShowWindow()
        {
            BehaviorDesignerWindow window = EditorWindow.GetWindow<BehaviorDesignerWindow>(false, "Behavior Designer");
            window.wantsMouseMove = true;
            window.minSize = new Vector2(700f, 100f);
            window.Init();
            BehaviorDesignerPreferences.InitPrefernces();
            if (!BehaviorDesignerPreferences.GetBool(BDPreferences.ShowWelcomeScreen))
                return;
            WelcomeScreen.ShowWindow();
        }

        public void OnEnable()
        {
            this.mIsPlaying = EditorApplication.isPlaying;
            this.mSizesInitialized = false;
            this.Repaint();
            this.mGraphZoomMultiplier = BehaviorDesignerPreferences.GetFloat(BDPreferences.ZoomSpeedMultiplier);
            EditorApplication.projectChanged += this.OnProjectWindowChange;
            EditorApplication.playModeStateChanged += new Action<PlayModeStateChange>(this.OnPlaymodeStateChange);
            // ISSUE: method pointer
            Undo.undoRedoPerformed = this.OnUndoRedo;
            this.Init();
            this.SetBehaviorManager();
        }

        public void OnFocus()
        {
            BehaviorDesignerWindow.instance = this;
            this.wantsMouseMove = true;
            this.Init();
            if (!this.mLockActiveGameObject)
            {
                this.mActiveObject = Selection.activeObject;
                this.ReloadPreviousBehavior();
            }
            else if (this.mActiveBehaviorSource == null)
                this.ReloadPreviousBehavior();

            this.UpdateGraphStatus();
            if (!this.mShowFindDialogue)
                return;
            this.Find();
        }

        public void OnSelectionChange()
        {
            if (!this.mLockActiveGameObject)
                this.UpdateTree(false);
            else
                this.ReloadPreviousBehavior();
            this.UpdateGraphStatus();
        }

        public void OnProjectWindowChange()
        {
            this.ReloadPreviousBehavior();
            this.ClearBreadcrumbMenu();
        }

        private void ReloadPreviousBehavior()
        {
            if (this.mActiveObject != (UnityEngine.Object)null)
            {
                if ((bool)(UnityEngine.Object)(this.mActiveObject as GameObject))
                {
                    GameObject mActiveObject = this.mActiveObject as GameObject;
                    int index1 = -1;
                    Behavior[] components = mActiveObject.GetComponents<Behavior>();
                    for (int index2 = 0; index2 < components.Length; ++index2)
                    {
                        if (((UnityEngine.Object)components[index2]).GetInstanceID() == this.mActiveBehaviorID)
                        {
                            index1 = index2;
                            break;
                        }
                    }

                    if (index1 != -1)
                        this.LoadBehavior(components[index1].GetBehaviorSource(), true, false);
                    else if (((IEnumerable<Behavior>)components).Count<Behavior>() > 0)
                    {
                        this.LoadBehavior(components[0].GetBehaviorSource(), true, false);
                    }
                    else
                    {
                        if (!((UnityEngine.Object)this.mGraphDesigner != (UnityEngine.Object)null))
                            return;
                        this.ClearGraph();
                    }
                }
                else if (this.mActiveObject is ExternalBehavior)
                {
                    ExternalBehavior mActiveObject = this.mActiveObject as ExternalBehavior;
                    BehaviorSource behaviorSource = mActiveObject.BehaviorSource;
                    if (mActiveObject.BehaviorSource.Owner == null)
                        mActiveObject.BehaviorSource.Owner = (IBehavior)mActiveObject;
                    this.LoadBehavior(behaviorSource, true, false);
                }
                else
                {
                    if (!((UnityEngine.Object)this.mGraphDesigner != (UnityEngine.Object)null))
                        return;
                    this.mActiveObject = (UnityEngine.Object)null;
                    this.ClearGraph();
                }
            }
            else
            {
                if (!((UnityEngine.Object)this.mGraphDesigner != (UnityEngine.Object)null))
                    return;
                this.ClearGraph();
                this.Repaint();
            }
        }

        private void UpdateTree(bool firstLoad)
        {
            bool flag1 = firstLoad;
            if (Selection.activeObject != (UnityEngine.Object)null)
            {
                bool loadPrevBehavior = false;
                if (!((object)Selection.activeObject).Equals((object)this.mActiveObject))
                {
                    this.mActiveObject = Selection.activeObject;
                    flag1 = true;
                }

                BehaviorSource behaviorSource = (BehaviorSource)null;
                GameObject mActiveObject1 = this.mActiveObject as GameObject;
                if ((UnityEngine.Object)mActiveObject1 != (UnityEngine.Object)null && (UnityEngine.Object)mActiveObject1.GetComponent<Behavior>() != (UnityEngine.Object)null)
                {
                    if (flag1)
                    {
                        if (((object)this.mActiveObject).Equals((object)this.mPrevActiveObject) && this.mActiveBehaviorID != -1)
                        {
                            loadPrevBehavior = true;
                            int index1 = -1;
                            Behavior[] components = (this.mActiveObject as GameObject).GetComponents<Behavior>();
                            for (int index2 = 0; index2 < components.Length; ++index2)
                            {
                                if (((UnityEngine.Object)components[index2]).GetInstanceID() == this.mActiveBehaviorID)
                                {
                                    index1 = index2;
                                    break;
                                }
                            }

                            if (index1 != -1)
                                behaviorSource = mActiveObject1.GetComponents<Behavior>()[index1].GetBehaviorSource();
                            else if (((IEnumerable<Behavior>)components).Count<Behavior>() > 0)
                                behaviorSource = mActiveObject1.GetComponents<Behavior>()[0].GetBehaviorSource();
                        }
                        else
                            behaviorSource = mActiveObject1.GetComponents<Behavior>()[0].GetBehaviorSource();
                    }
                    else
                    {
                        Behavior[] components = mActiveObject1.GetComponents<Behavior>();
                        bool flag2 = false;
                        if (this.mActiveBehaviorSource != null)
                        {
                            for (int index = 0; index < components.Length; ++index)
                            {
                                if (((object)components[index]).Equals((object)this.mActiveBehaviorSource.Owner))
                                {
                                    flag2 = true;
                                    break;
                                }
                            }
                        }

                        if (!flag2)
                        {
                            behaviorSource = mActiveObject1.GetComponents<Behavior>()[0].GetBehaviorSource();
                        }
                        else
                        {
                            behaviorSource = this.mActiveBehaviorSource;
                            loadPrevBehavior = true;
                        }
                    }
                }
                else if (this.mActiveObject is ExternalBehavior)
                {
                    ExternalBehavior mActiveObject2 = this.mActiveObject as ExternalBehavior;
                    if (mActiveObject2.BehaviorSource.Owner == null)
                        mActiveObject2.BehaviorSource.Owner = (IBehavior)mActiveObject2;
                    if (flag1 && ((object)this.mActiveObject).Equals((object)this.mPrevActiveObject))
                        loadPrevBehavior = true;
                    behaviorSource = mActiveObject2.BehaviorSource;
                }
                else
                    this.mPrevActiveObject = (UnityEngine.Object)null;

                if (behaviorSource != null)
                {
                    this.LoadBehavior(behaviorSource, loadPrevBehavior, false);
                }
                else
                {
                    if (behaviorSource != null)
                        return;
                    this.ClearGraph();
                }
            }
            else
            {
                if (this.mActiveObject != (UnityEngine.Object)null && this.mActiveBehaviorSource != null)
                    this.mPrevActiveObject = this.mActiveObject;
                this.mActiveObject = (UnityEngine.Object)null;
                this.ClearGraph();
            }
        }

        private void Init()
        {
            if ((UnityEngine.Object)this.mTaskList == (UnityEngine.Object)null)
                this.mTaskList = ScriptableObject.CreateInstance<TaskList>();
            if ((UnityEngine.Object)this.mVariableInspector == (UnityEngine.Object)null)
                this.mVariableInspector = ScriptableObject.CreateInstance<VariableInspector>();
            if ((UnityEngine.Object)this.mGraphDesigner == (UnityEngine.Object)null)
                this.mGraphDesigner = ScriptableObject.CreateInstance<GraphDesigner>();
            if ((UnityEngine.Object)this.mTaskInspector == (UnityEngine.Object)null)
                this.mTaskInspector = ScriptableObject.CreateInstance<TaskInspector>();
            if ((UnityEngine.Object)this.mGridMaterial == (UnityEngine.Object)null)
            {
                this.mGridMaterial = new Material(Shader.Find("Hidden/Behavior Designer/Grid"));
                this.mGridMaterial.hideFlags = HideFlags.HideAndDontSave;
                this.mGridMaterial.shader.hideFlags = HideFlags.HideAndDontSave;
            }

            this.mTaskList.Init();
            FieldInspector.Init();
            this.ClearBreadcrumbMenu();
        }

        public void UpdateGraphStatus()
        {
            if (this.mActiveObject == (UnityEngine.Object)null || (UnityEngine.Object)this.mGraphDesigner == (UnityEngine.Object)null ||
                (UnityEngine.Object)(this.mActiveObject as GameObject) == (UnityEngine.Object)null && (UnityEngine.Object)(this.mActiveObject as ExternalBehavior) == (UnityEngine.Object)null)
                this.mGraphStatus = "Select a GameObject";
            else if ((UnityEngine.Object)(this.mActiveObject as GameObject) != (UnityEngine.Object)null && object.ReferenceEquals((object)(this.mActiveObject as GameObject).GetComponent<Behavior>(), (object)null))
                this.mGraphStatus = "Right Click, Add a Behavior Tree Component";
            else if (this.ViewOnlyMode() && this.mActiveBehaviorSource != null)
            {
                ExternalBehavior externalBehavior = (this.mActiveBehaviorSource.Owner.GetObject() as Behavior).ExternalBehavior;
                if ((UnityEngine.Object)externalBehavior != (UnityEngine.Object)null)
                    this.mGraphStatus = ((object)externalBehavior.BehaviorSource).ToString() + " (View Only Mode)";
                else
                    this.mGraphStatus = ((object)this.mActiveBehaviorSource).ToString() + " (View Only Mode)";
            }
            else if (!this.mGraphDesigner.HasEntryNode())
                this.mGraphStatus = "Add a Task";
            else if (this.IsReferencingTasks())
            {
                this.mGraphStatus = "Select tasks to reference (right click to exit)";
            }
            else
            {
                if (this.mActiveBehaviorSource == null || this.mActiveBehaviorSource.Owner == null || !(this.mActiveBehaviorSource.Owner.GetObject() != (UnityEngine.Object)null))
                    return;
                if (this.mExternalParent != null)
                    this.mGraphStatus = ((object)this.mExternalParent).ToString() + " (Editing External Behavior)";
                else
                    this.mGraphStatus = ((object)this.mActiveBehaviorSource).ToString();
            }
        }

        private void BuildBreadcrumbMenus(BehaviorDesignerWindow.BreadcrumbMenuType menuType)
        {
            Dictionary<BehaviorSource, string> dictionary1 = new Dictionary<BehaviorSource, string>();
            Dictionary<string, int> dictionary2 = new Dictionary<string, int>();
            HashSet<UnityEngine.Object> objectSet = new HashSet<UnityEngine.Object>();
            List<BehaviorSource> behaviorSourceList = new List<BehaviorSource>();
            Behavior[] objectsOfTypeAll1 = Resources.FindObjectsOfTypeAll(typeof(Behavior)) as Behavior[];
            for (int index = objectsOfTypeAll1.Length - 1; index > -1; --index)
            {
                BehaviorSource behaviorSource = objectsOfTypeAll1[index].GetBehaviorSource();
                if (behaviorSource.Owner == null)
                    behaviorSource.Owner = (IBehavior)objectsOfTypeAll1[index];
                behaviorSourceList.Add(behaviorSource);
            }

            ExternalBehavior[] objectsOfTypeAll2 = Resources.FindObjectsOfTypeAll(typeof(ExternalBehavior)) as ExternalBehavior[];
            for (int index = objectsOfTypeAll2.Length - 1; index > -1; --index)
            {
                BehaviorSource behaviorSource = objectsOfTypeAll2[index].GetBehaviorSource();
                if (behaviorSource.Owner == null)
                    behaviorSource.Owner = (IBehavior)objectsOfTypeAll2[index];
                behaviorSourceList.Add(behaviorSource);
            }

            behaviorSourceList.Sort((IComparer<BehaviorSource>)new AlphanumComparator<BehaviorSource>());
            for (int index = 0; index < behaviorSourceList.Count; ++index)
            {
                UnityEngine.Object @object = behaviorSourceList[index].Owner.GetObject();
                if (menuType == BehaviorDesignerWindow.BreadcrumbMenuType.Behavior)
                {
                    if (@object is Behavior)
                    {
                        if (!((object)((Component)(@object as Behavior)).gameObject).Equals((object)this.mActiveObject))
                            continue;
                    }
                    else if (!((object)(@object as ExternalBehavior)).Equals((object)this.mActiveObject))
                        continue;
                }

                if (menuType == BehaviorDesignerWindow.BreadcrumbMenuType.GameObject && @object is Behavior)
                {
                    if (!objectSet.Contains((UnityEngine.Object)((Component)(@object as Behavior)).gameObject))
                        objectSet.Add((UnityEngine.Object)((Component)(@object as Behavior)).gameObject);
                    else
                        continue;
                }

                string key = string.Empty;
                if (@object is Behavior)
                {
                    switch (menuType)
                    {
                        case BehaviorDesignerWindow.BreadcrumbMenuType.GameObjectBehavior:
                            key = ((object)behaviorSourceList[index]).ToString();
                            break;
                        case BehaviorDesignerWindow.BreadcrumbMenuType.GameObject:
                            key = ((Component)(@object as Behavior)).gameObject.name;
                            break;
                        case BehaviorDesignerWindow.BreadcrumbMenuType.Behavior:
                            key = behaviorSourceList[index].behaviorName;
                            break;
                    }

                    if (!AssetDatabase.GetAssetPath(@object).Equals(string.Empty))
                        key += " (prefab)";
                }
                else
                    key = ((object)behaviorSourceList[index]).ToString() + " (external)";

                int num1 = 0;
                if (dictionary2.TryGetValue(key, out num1))
                {
                    int num2;
                    dictionary2[key] = num2 = num1 + 1;
                    key += string.Format(" ({0})", (object)(num2 + 1));
                }
                else
                    dictionary2.Add(key, 0);

                dictionary1.Add(behaviorSourceList[index], key);
            }

            switch (menuType)
            {
                case BehaviorDesignerWindow.BreadcrumbMenuType.GameObjectBehavior:
                    this.mBreadcrumbGameObjectBehaviorMenu = new GenericMenu();
                    break;
                case BehaviorDesignerWindow.BreadcrumbMenuType.GameObject:
                    this.mBreadcrumbGameObjectMenu = new GenericMenu();
                    break;
                case BehaviorDesignerWindow.BreadcrumbMenuType.Behavior:
                    this.mBreadcrumbBehaviorMenu = new GenericMenu();
                    break;
            }

            foreach (KeyValuePair<BehaviorSource, string> keyValuePair in dictionary1)
            {
                switch (menuType)
                {
                    case BehaviorDesignerWindow.BreadcrumbMenuType.GameObjectBehavior:
                        // ISSUE: method pointer
                        this.mBreadcrumbGameObjectBehaviorMenu.AddItem(new GUIContent(keyValuePair.Value), ((object)keyValuePair.Key).Equals((object)this.mActiveBehaviorSource), this.BehaviorSelectionCallback, (object)keyValuePair.Key);
                        continue;
                    case BehaviorDesignerWindow.BreadcrumbMenuType.GameObject:
                        bool flag = !(keyValuePair.Key.Owner.GetObject() is ExternalBehavior)
                            ? ((object)((Component)(keyValuePair.Key.Owner.GetObject() as Behavior)).gameObject).Equals((object)this.mActiveObject)
                            : ((object)(keyValuePair.Key.Owner.GetObject() as ExternalBehavior).GetObject()).Equals((object)this.mActiveObject);
                        // ISSUE: method pointer
                        this.mBreadcrumbGameObjectMenu.AddItem(new GUIContent(keyValuePair.Value), flag, this.BehaviorSelectionCallback, (object)keyValuePair.Key);
                        continue;
                    case BehaviorDesignerWindow.BreadcrumbMenuType.Behavior:
                        // ISSUE: method pointer
                        this.mBreadcrumbBehaviorMenu.AddItem(new GUIContent(keyValuePair.Value), ((object)keyValuePair.Key).Equals((object)this.mActiveBehaviorSource), this.BehaviorSelectionCallback, (object)keyValuePair.Key);
                        continue;
                    default:
                        continue;
                }
            }
        }

        private void ClearBreadcrumbMenu()
        {
            this.mBreadcrumbGameObjectBehaviorMenu = (GenericMenu)null;
            this.mBreadcrumbGameObjectMenu = (GenericMenu)null;
            this.mBreadcrumbBehaviorMenu = (GenericMenu)null;
        }

        private void BuildRightClickMenu(NodeDesigner clickedNode)
        {
            if (this.mActiveObject == (UnityEngine.Object)null)
                return;
            this.mRightClickMenu = new GenericMenu();
            if ((UnityEngine.Object)clickedNode == (UnityEngine.Object)null && (!EditorApplication.isPlaying || (bool)(UnityEngine.Object)(this.mActiveObject as ExternalBehavior)) && !this.ViewOnlyMode())
            {
                // ISSUE: method pointer
                this.mTaskList.AddTasksToMenu(ref this.mRightClickMenu, (System.Type)null, "Add Task", this.AddTaskCallback);
                if (this.mCopiedTasks != null && this.mCopiedTasks.Count > 0)
                {
                    // ISSUE: method pointer
                    this.mRightClickMenu.AddItem(new GUIContent("Paste Tasks"), false, this.PasteNodes);
                }
                else
                    this.mRightClickMenu.AddDisabledItem(new GUIContent("Paste Tasks"));
            }

            if ((UnityEngine.Object)clickedNode != (UnityEngine.Object)null && !clickedNode.IsEntryDisplay)
            {
                if (this.mGraphDesigner.SelectedNodes.Count == 1)
                {
                    // ISSUE: method pointer
                    this.mRightClickMenu.AddItem(new GUIContent("Edit Script"), false, this.OpenInFileEditor, (object)clickedNode);
                    // ISSUE: method pointer
                    this.mRightClickMenu.AddItem(new GUIContent("Locate Script"), false, this.SelectInProject, (object)clickedNode);
                    if (!this.ViewOnlyMode())
                    {
                        // ISSUE: method pointer
                        this.mRightClickMenu.AddItem(new GUIContent(!clickedNode.Task.Disabled ? "Disable" : "Enable"), false, this.ToggleEnableState, (object)clickedNode);
                        if (clickedNode.IsParent)
                        {
                            // ISSUE: method pointer
                            this.mRightClickMenu.AddItem(new GUIContent(!clickedNode.Task.NodeData.Collapsed ? "Collapse" : "Expand"), false, this.ToggleCollapseState, (object)clickedNode);
                        }

                        // ISSUE: method pointer
                        this.mRightClickMenu.AddItem(new GUIContent(!clickedNode.Task.NodeData.IsBreakpoint ? "Set Breakpoint" : "Remove Breakpoint"), false, this.ToggleBreakpoint, (object)clickedNode);
                    }
                }

                if ((!EditorApplication.isPlaying || (bool)(UnityEngine.Object)(this.mActiveObject as ExternalBehavior)) && !this.ViewOnlyMode())
                {
                    // ISSUE: method pointer
                    this.mRightClickMenu.AddItem(new GUIContent(string.Format("Copy Task{0}", this.mGraphDesigner.SelectedNodes.Count <= 1 ? (object)string.Empty : (object)"s")), false, this.CopyNodes);
                    if (this.mCopiedTasks != null && this.mCopiedTasks.Count > 0)
                    {
                        // ISSUE: method pointer
                        this.mRightClickMenu.AddItem(new GUIContent(string.Format("Paste Task{0}", this.mCopiedTasks.Count <= 1 ? (object)string.Empty : (object)"s")), false, this.PasteNodes);
                    }
                    else
                        this.mRightClickMenu.AddDisabledItem(new GUIContent("Paste Tasks"));

                    // ISSUE: method pointer
                    this.mRightClickMenu.AddItem(new GUIContent(string.Format("Duplicate Task{0}", this.mGraphDesigner.SelectedNodes.Count <= 1 ? (object)string.Empty : (object)"s")), false, this.DuplicateNodes);
                    if (this.mGraphDesigner.SelectedNodes.Count > 0)
                    {
                        // ISSUE: method pointer
                        this.mTaskList.AddTasksToMenu(ref this.mRightClickMenu, this.mGraphDesigner.SelectedNodes.Count != 1 ? (System.Type)null : ((object)this.mGraphDesigner.SelectedNodes[0].Task).GetType(), "Replace", this.ReplaceTasksCallback);
                    }

                    // ISSUE: method pointer
                    this.mRightClickMenu.AddItem(new GUIContent(string.Format("Delete Task{0}", this.mGraphDesigner.SelectedNodes.Count <= 1 ? (object)string.Empty : (object)"s")), false, this.DeleteNodes);
                }
            }

            if ((!EditorApplication.isPlaying || (bool)(UnityEngine.Object)(this.mActiveObject as ExternalBehavior)) && (UnityEngine.Object)(this.mActiveObject as GameObject) != (UnityEngine.Object)null)
            {
                if ((UnityEngine.Object)clickedNode != (UnityEngine.Object)null && !clickedNode.IsEntryDisplay)
                    this.mRightClickMenu.AddSeparator(string.Empty);
                // ISSUE: method pointer
                this.mRightClickMenu.AddItem(new GUIContent("Add Behavior Tree"), false, this.AddBehavior);
                if (this.mActiveBehaviorSource != null)
                {
                    // ISSUE: method pointer
                    this.mRightClickMenu.AddItem(new GUIContent("Remove Behavior Tree"), false, this.RemoveBehavior);
                    // ISSUE: method pointer
                    this.mRightClickMenu.AddItem(new GUIContent("Save As External Behavior Tree"), false, this.SaveAsAsset);
                }
            }

            this.mShowRightClickMenu = this.mRightClickMenu.GetItemCount() > 0;
        }

        public void Update()
        {
            if (!this.mTakingScreenshot)
                return;
            this.Repaint();
        }

        public void OnGUI()
        {
            if (Event.current.type == EventType.MouseMove || Event.current.type == EventType.Repaint)
                this.mCurrentMousePosition = Event.current.mousePosition;
            this.SetupSizes();
            if (!this.mSizesInitialized)
            {
                this.mSizesInitialized = true;
                if (!this.mLockActiveGameObject || this.mActiveObject == (UnityEngine.Object)null)
                    this.UpdateTree(true);
                else
                    this.ReloadPreviousBehavior();
            }

            this.Draw();
            this.HandleEvents();
        }

        public void OnPlaymodeStateChange(PlayModeStateChange change) => this.OnPlaymodeStateChange();

        public void OnPlaymodeStateChange()
        {
            if (EditorApplication.isPlaying && !EditorApplication.isPaused)
            {
                if ((UnityEngine.Object)this.mBehaviorManager == (UnityEngine.Object)null)
                {
                    this.SetBehaviorManager();
                    if ((UnityEngine.Object)this.mBehaviorManager == (UnityEngine.Object)null)
                        return;
                }

                if (!((UnityEngine.Object)this.mBehaviorManager.BreakpointTree != (UnityEngine.Object)null) || !this.mEditorAtBreakpoint)
                    return;
                this.mEditorAtBreakpoint = false;
                this.mBehaviorManager.BreakpointTree = (Behavior)null;
            }
            else if (EditorApplication.isPlaying && EditorApplication.isPaused)
            {
                if (!((UnityEngine.Object)this.mBehaviorManager != (UnityEngine.Object)null) || !((UnityEngine.Object)this.mBehaviorManager.BreakpointTree != (UnityEngine.Object)null))
                    return;
                if (!this.mEditorAtBreakpoint)
                {
                    this.mEditorAtBreakpoint = true;
                    if (!BehaviorDesignerPreferences.GetBool(BDPreferences.SelectOnBreakpoint) || this.mLockActiveGameObject)
                        return;
                    Selection.activeObject = (UnityEngine.Object)this.mBehaviorManager.BreakpointTree;
                    this.LoadBehavior(this.mBehaviorManager.BreakpointTree.GetBehaviorSource(), this.mActiveBehaviorSource == this.mBehaviorManager.BreakpointTree.GetBehaviorSource(), false);
                }
                else
                {
                    this.mEditorAtBreakpoint = false;
                    this.mBehaviorManager.BreakpointTree = (Behavior)null;
                }
            }
            else
            {
                if (EditorApplication.isPlaying)
                    return;
                this.mBehaviorManager = (BehaviorManager)null;
            }
        }

        private void SetBehaviorManager()
        {
            this.mBehaviorManager = BehaviorManager.instance;
            if ((UnityEngine.Object)this.mBehaviorManager == (UnityEngine.Object)null)
                return;
            BehaviorManager mBehaviorManager = this.mBehaviorManager;
            // ISSUE: method pointer
            mBehaviorManager.OnTaskBreakpoint += OnTaskBreakpoint;
            this.mUpdateNodeTaskMap = true;
        }

        public void OnTaskBreakpoint()
        {
            EditorApplication.isPaused = true;
            this.Repaint();
        }

        private void OnPreferenceChange(BDPreferences pref, object value)
        {
            switch (pref)
            {
                case BDPreferences.CompactMode:
                    this.mGraphDesigner.GraphDirty();
                    break;
                case BDPreferences.BinarySerialization:
                    this.SaveBehavior();
                    break;
                case BDPreferences.ErrorChecking:
                    this.CheckForErrors();
                    break;
                default:
                    switch (pref - 20)
                    {
                        case BDPreferences.ShowWelcomeScreen:
                            GizmoManager.UpdateAllGizmos();
                            return;
                        case BDPreferences.ShowHierarchyIcon:
                            this.mGraphZoomMultiplier = (float)value;
                            return;
                        default:
                            if (pref != BDPreferences.ShowSceneIcon)
                                return;
                            goto case BDPreferences.ShowWelcomeScreen;
                    }
            }
        }

        public void OnInspectorUpdate()
        {
            if ((UnityEngine.Object)BehaviorDesignerWindow.instance == (UnityEngine.Object)null)
                this.OnFocus();
            if (this.mStepApplication)
            {
                EditorApplication.Step();
                this.mStepApplication = false;
            }

            if (EditorApplication.isPlaying && !EditorApplication.isPaused && this.mActiveBehaviorSource != null && (UnityEngine.Object)this.mBehaviorManager != (UnityEngine.Object)null)
            {
                if (this.mUpdateNodeTaskMap)
                    this.UpdateNodeTaskMap();
                if ((UnityEngine.Object)this.mBehaviorManager.BreakpointTree != (UnityEngine.Object)null)
                    this.mBehaviorManager.BreakpointTree = (Behavior)null;
                this.Repaint();
            }

            if (Application.isPlaying && (UnityEngine.Object)this.mBehaviorManager == (UnityEngine.Object)null)
                this.SetBehaviorManager();
            if ((UnityEngine.Object)this.mBehaviorManager != (UnityEngine.Object)null && this.mBehaviorManager.Dirty)
            {
                if (this.mActiveBehaviorSource != null)
                    this.ReloadPreviousBehavior();
                this.mBehaviorManager.Dirty = false;
            }

            if (!EditorApplication.isPlaying && this.mIsPlaying)
                this.ReloadPreviousBehavior();
            this.mIsPlaying = EditorApplication.isPlaying;
            this.UpdateGraphStatus();
            this.UpdateCheck();
        }

        private void UpdateNodeTaskMap()
        {
            if (!this.mUpdateNodeTaskMap || !((UnityEngine.Object)this.mBehaviorManager != (UnityEngine.Object)null))
                return;
            List<Task> taskList = this.mBehaviorManager.GetTaskList(this.mActiveBehaviorSource.Owner as Behavior);
            if (taskList == null)
                return;
            this.mNodeDesignerTaskMap = new Dictionary<NodeDesigner, Task>();
            for (int index = 0; index < taskList.Count; ++index)
            {
                NodeDesigner nodeDesigner = taskList[index].NodeData.NodeDesigner as NodeDesigner;
                if ((UnityEngine.Object)nodeDesigner != (UnityEngine.Object)null && !this.mNodeDesignerTaskMap.ContainsKey(nodeDesigner))
                    this.mNodeDesignerTaskMap.Add(nodeDesigner, taskList[index]);
            }

            this.mUpdateNodeTaskMap = false;
        }

        private bool Draw()
        {
            bool flag = false;
            Color color = GUI.color;
            Color backgroundColor = GUI.backgroundColor;
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            if (this.DrawGraphArea())
                flag = true;
            this.DrawFileToolbar();
            this.DrawDebugToolbar();
            this.DrawPropertiesBox();
            this.DrawQuickTaskList();
            this.DrawFindDialogue();
            this.DrawPreferencesPane();
            if (this.mTakingScreenshot)
                GUI.DrawTexture(new Rect(0.0f, 0.0f, this.position.width, this.position.height + 22f), (Texture)BehaviorDesignerUtility.ScreenshotBackgroundTexture, (ScaleMode)0, false);
            GUI.color = color;
            GUI.backgroundColor = backgroundColor;
            return flag;
        }

        private void DrawFileToolbar()
        {
            GUILayout.BeginArea(this.mFileToolBarRect, EditorStyles.toolbar);
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            if (GUILayout.Button((Texture)BehaviorDesignerUtility.HistoryBackwardTexture,
                    EditorStyles.toolbarButton,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(20f)
                    }) && (this.mBehaviorSourceHistoryIndex > 0 || this.mActiveBehaviorSource == null && this.mBehaviorSourceHistoryIndex == 0))
            {
                BehaviorSource behaviorSource = (BehaviorSource)null;
                if (this.mActiveBehaviorSource == null)
                    ++this.mBehaviorSourceHistoryIndex;
                while (behaviorSource == null && this.mBehaviorSourceHistory.Count > 0 && this.mBehaviorSourceHistoryIndex > 0)
                {
                    --this.mBehaviorSourceHistoryIndex;
                    behaviorSource = this.BehaviorSourceFromIBehaviorHistory(this.mBehaviorSourceHistory[this.mBehaviorSourceHistoryIndex] as IBehavior);
                    if (behaviorSource == null || behaviorSource.Owner == null || behaviorSource.Owner.GetObject() == (UnityEngine.Object)null)
                    {
                        this.mBehaviorSourceHistory.RemoveAt(this.mBehaviorSourceHistoryIndex);
                        behaviorSource = (BehaviorSource)null;
                    }
                }

                if (behaviorSource != null)
                    this.LoadBehavior(behaviorSource, false);
            }

            if (GUILayout.Button((Texture)BehaviorDesignerUtility.HistoryForwardTexture,
                    EditorStyles.toolbarButton,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(20f)
                    }))
            {
                BehaviorSource behaviorSource = (BehaviorSource)null;
                if (this.mBehaviorSourceHistoryIndex < this.mBehaviorSourceHistory.Count - 1)
                {
                    ++this.mBehaviorSourceHistoryIndex;
                    while (behaviorSource == null && this.mBehaviorSourceHistoryIndex < this.mBehaviorSourceHistory.Count && this.mBehaviorSourceHistoryIndex > 0)
                    {
                        behaviorSource = this.BehaviorSourceFromIBehaviorHistory(this.mBehaviorSourceHistory[this.mBehaviorSourceHistoryIndex] as IBehavior);
                        if (behaviorSource == null || behaviorSource.Owner == null || behaviorSource.Owner.GetObject() == (UnityEngine.Object)null)
                        {
                            this.mBehaviorSourceHistory.RemoveAt(this.mBehaviorSourceHistoryIndex);
                            behaviorSource = (BehaviorSource)null;
                        }
                    }
                }

                if (behaviorSource != null)
                    this.LoadBehavior(behaviorSource, false);
            }

            if (GUILayout.Button("...",
                    EditorStyles.toolbarButton,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(22f)
                    }))
            {
                this.BuildBreadcrumbMenus(BehaviorDesignerWindow.BreadcrumbMenuType.GameObjectBehavior);
                this.mBreadcrumbGameObjectBehaviorMenu.ShowAsContext();
            }

            if (GUILayout.Button((UnityEngine.Object)(this.mActiveObject as GameObject) != (UnityEngine.Object)null || (UnityEngine.Object)(this.mActiveObject as ExternalBehavior) != (UnityEngine.Object)null ? this.mActiveObject.name : "(None Selected)",
                    EditorStyles.toolbarPopup,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(140f)
                    }))
            {
                this.BuildBreadcrumbMenus(BehaviorDesignerWindow.BreadcrumbMenuType.GameObject);
                this.mBreadcrumbGameObjectMenu.ShowAsContext();
            }

            if (GUILayout.Button(this.mActiveBehaviorSource == null ? "(None Selected)" : this.mActiveBehaviorSource.behaviorName,
                    EditorStyles.toolbarPopup,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(140f)
                    }) && this.mActiveBehaviorSource != null)
            {
                this.BuildBreadcrumbMenus(BehaviorDesignerWindow.BreadcrumbMenuType.Behavior);
                this.mBreadcrumbBehaviorMenu.ShowAsContext();
            }

            if (GUILayout.Button("Referenced Behaviors",
                    EditorStyles.toolbarPopup,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(140f)
                    }) && this.mActiveBehaviorSource != null)
            {
                List<BehaviorSource> referencedBehaviors = this.mGraphDesigner.FindReferencedBehaviors();
                if (referencedBehaviors.Count > 0)
                {
                    referencedBehaviors.Sort((IComparer<BehaviorSource>)new AlphanumComparator<BehaviorSource>());
                    this.mReferencedBehaviorsMenu = new GenericMenu();
                    for (int index = 0; index < referencedBehaviors.Count; ++index)
                    {
                        // ISSUE: method pointer
                        this.mReferencedBehaviorsMenu.AddItem(new GUIContent(((object)referencedBehaviors[index]).ToString()), false, this.BehaviorSelectionCallback, (object)referencedBehaviors[index]);
                    }

                    this.mReferencedBehaviorsMenu.ShowAsContext();
                }
            }

            if (GUILayout.Button("-",
                    EditorStyles.toolbarButton,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(22f)
                    }))
            {
                if (this.mActiveBehaviorSource != null)
                    this.RemoveBehavior();
                else
                    EditorUtility.DisplayDialog("Unable to Remove Behavior Tree", "No behavior tree selected.", "OK");
            }

            if (GUILayout.Button("+",
                    EditorStyles.toolbarButton,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(22f)
                    }))
            {
                if (this.mActiveObject != (UnityEngine.Object)null)
                    this.AddBehavior();
                else
                    EditorUtility.DisplayDialog("Unable to Add Behavior Tree", "No GameObject is selected.", "OK");
            }

            if (GUILayout.Button("Lock",
                    !this.mLockActiveGameObject ? EditorStyles.toolbarButton : BehaviorDesignerUtility.ToolbarButtonSelectionGUIStyle,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(42f)
                    }))
            {
                if (this.mActiveObject != (UnityEngine.Object)null)
                {
                    this.mLockActiveGameObject = !this.mLockActiveGameObject;
                    if (!this.mLockActiveGameObject)
                        this.UpdateTree(false);
                }
                else if (this.mLockActiveGameObject)
                    this.mLockActiveGameObject = false;
                else
                    EditorUtility.DisplayDialog("Unable to Lock GameObject", "No GameObject is selected.", "OK");
            }

            GUI.enabled = this.mActiveBehaviorSource == null || this.mExternalParent == null;
            if (GUILayout.Button("Export",
                    EditorStyles.toolbarButton,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(46f)
                    }))
            {
                if (this.mActiveBehaviorSource != null)
                {
                    if ((bool)(UnityEngine.Object)(this.mActiveBehaviorSource.Owner.GetObject() as Behavior))
                        this.SaveAsAsset();
                    else
                        this.SaveAsPrefab();
                }
                else
                    EditorUtility.DisplayDialog("Unable to Save Behavior Tree", "Select a behavior tree from within the scene.", "OK");
            }

            GUI.enabled = true;
            if (GUILayout.Button("Find",
                    !this.mShowFindDialogue ? EditorStyles.toolbarButton : BehaviorDesignerUtility.ToolbarButtonSelectionGUIStyle,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(40f)
                    }))
            {
                this.mShowFindDialogue = !this.mShowFindDialogue;
                if (this.mShowFindDialogue && this.mShowPrefPane)
                    this.mShowPrefPane = false;
                else if (!this.mShowFindDialogue)
                    this.ClearFindResults();
            }

            if (GUILayout.Button("Preferences",
                    !this.mShowPrefPane ? EditorStyles.toolbarButton : BehaviorDesignerUtility.ToolbarButtonSelectionGUIStyle,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(80f)
                    }))
            {
                this.mShowPrefPane = !this.mShowPrefPane;
                if (this.mShowPrefPane && this.mShowFindDialogue)
                {
                    this.mShowFindDialogue = false;
                    this.ClearFindResults();
                }
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private void DrawDebugToolbar()
        {
            GUILayout.BeginArea(this.mDebugToolBarRect, EditorStyles.toolbar);
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            if (GUILayout.Button((Texture)BehaviorDesignerUtility.PlayTexture,
                    !EditorApplication.isPlaying ? EditorStyles.toolbarButton : BehaviorDesignerUtility.ToolbarButtonSelectionGUIStyle,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(40f)
                    }))
                EditorApplication.isPlaying = !EditorApplication.isPlaying;
            if (GUILayout.Button((Texture)BehaviorDesignerUtility.PauseTexture,
                    !EditorApplication.isPaused ? EditorStyles.toolbarButton : BehaviorDesignerUtility.ToolbarButtonSelectionGUIStyle,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(40f)
                    }))
                EditorApplication.isPaused = !EditorApplication.isPaused;
            if (GUILayout.Button((Texture)BehaviorDesignerUtility.StepTexture,
                    EditorStyles.toolbarButton,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(40f)
                    }) && EditorApplication.isPlaying)
                this.mStepApplication = true;
            if (this.mErrorDetails != null && this.mErrorDetails.Count > 0)
            {
                if (GUILayout.Button(new GUIContent(this.mErrorDetails.Count.ToString() + " Error" + (this.mErrorDetails.Count <= 1 ? (object)string.Empty : (object)"s"), (Texture)BehaviorDesignerUtility.SmallErrorIconTexture),
                        BehaviorDesignerUtility.ToolbarButtonLeftAlignGUIStyle,
                        new GUILayoutOption[1]
                        {
                            GUILayout.Width(85f)
                        }))
                    ErrorWindow.ShowWindow();
            }

            GUILayout.FlexibleSpace();
            try
            {
                if (new Version("1.7.7").CompareTo(new Version(this.LatestVersion)) < 0)
                    GUILayout.Label("Behavior Designer " + this.LatestVersion + " is now available.", BehaviorDesignerUtility.ToolbarLabelGUIStyle, Array.Empty<GUILayoutOption>());
            }
            catch (Exception ex)
            {
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DrawFindDialogue()
        {
            if (!this.mShowFindDialogue)
                return;
            GUILayout.BeginArea(this.mFindDialogueRect, BehaviorDesignerUtility.PreferencesPaneGUIStyle);
            EditorGUILayout.LabelField("Find", BehaviorDesignerUtility.LabelTitleGUIStyle, Array.Empty<GUILayoutOption>());
            GUIContent guiContent = new GUIContent("Task");
            Vector2 vector2_1 = GUI.skin.label.CalcSize(guiContent);
            float labelWidth1 = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = vector2_1.x + 50f;
            this.mFindTaskValue = EditorGUILayout.TextField(guiContent, this.mFindTaskValue, Array.Empty<GUILayoutOption>());
            EditorGUIUtility.labelWidth = labelWidth1;
            string[] names = (string[])null;
            int globalStartIndex = -1;
            int variablesOfType = FieldInspector.GetVariablesOfType((System.Type)null,
                this.mFindSharedVariable != null && this.mFindSharedVariable.IsGlobal,
                this.mFindSharedVariable == null ? string.Empty : this.mFindSharedVariable.Name,
                this.mActiveBehaviorSource,
                out names,
                ref globalStartIndex,
                true,
                false);
            if (names == null || names.Length == 0)
                names = new string[1] { "(None)" };
            guiContent.text = "Variable";
            Vector2 vector2_2 = GUI.skin.label.CalcSize(guiContent);
            float labelWidth2 = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = vector2_2.x + 30f;
            int index = EditorGUILayout.Popup("Variable", variablesOfType, names, BehaviorDesignerUtility.SharedVariableToolbarPopup, Array.Empty<GUILayoutOption>());
            EditorGUIUtility.labelWidth = labelWidth2;
            if (index != variablesOfType)
                this.mFindSharedVariable = index != 0 ? (globalStartIndex == -1 || index < globalStartIndex ? this.mActiveBehaviorSource.GetVariable(names[index]) : GlobalVariables.Instance.GetVariable(names[index].Substring(8, names[index].Length - 8))) : (SharedVariable)null;
            GUILayout.Space(6f);
            EditorGUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear",
                    EditorStyles.toolbarButton,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(80f)
                    }))
                this.ClearFindResults();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.EndArea();
            if (!GUI.changed)
                return;
            this.Find();
        }

        private void Find() => this.mGraphDesigner.Find(this.mFindTaskValue, this.mFindSharedVariable);

        private void ClearFindResults()
        {
            if (string.IsNullOrEmpty(this.mFindTaskValue) && this.mFindSharedVariable == null)
                return;
            this.mFindTaskValue = string.Empty;
            this.mFindSharedVariable = (SharedVariable)null;
        }

        private void DrawQuickTaskList()
        {
            if (!this.mShowQuickTaskList)
                return;
            GUILayout.BeginArea(this.mQuickTaskListRect, BehaviorDesignerUtility.PreferencesPaneGUIStyle);
            this.mTaskList.DrawQuickTaskList(this, !this.ViewOnlyMode());
            GUILayout.EndArea();
        }

        private void DrawPreferencesPane()
        {
            if (!this.mShowPrefPane)
                return;
            GUILayout.BeginArea(this.mPreferencesPaneRect, BehaviorDesignerUtility.PreferencesPaneGUIStyle);
            BehaviorDesignerPreferences.DrawPreferencesPane(new PreferenceChangeHandler(this.OnPreferenceChange));
            GUILayout.EndArea();
        }

        private void DrawPropertiesBox()
        {
            GUILayout.BeginArea(this.mPropertyToolbarRect, EditorStyles.toolbar);
            int toolbarSelection = this.mBehaviorToolbarSelection;
            this.mBehaviorToolbarSelection = GUILayout.Toolbar(this.mBehaviorToolbarSelection, this.mBehaviorToolbarStrings, EditorStyles.toolbarButton, Array.Empty<GUILayoutOption>());
            GUILayout.EndArea();
            GUILayout.BeginArea(this.mPropertyBoxRect, BehaviorDesignerUtility.PropertyBoxGUIStyle);
            if (this.mBehaviorToolbarSelection == 0)
            {
                if (this.mActiveBehaviorSource != null)
                {
                    GUILayout.Space(3f);
                    BehaviorSource behaviorSource = this.mExternalParent == null ? this.mActiveBehaviorSource : this.mExternalParent;
                    if ((UnityEngine.Object)(behaviorSource.Owner as Behavior) != (UnityEngine.Object)null)
                    {
                        bool externalModification = false;
                        bool flag = false;
                        if (BehaviorInspector.DrawInspectorGUI(behaviorSource.Owner as Behavior, new SerializedObject((UnityEngine.Object)(behaviorSource.Owner as Behavior)), false, ref externalModification, ref flag, ref flag))
                        {
                            BehaviorDesignerUtility.SetObjectDirty(behaviorSource.Owner.GetObject());
                            if (externalModification)
                                this.LoadBehavior(behaviorSource, false, false);
                        }
                    }
                    else
                    {
                        bool showVariables = false;
                        ExternalBehaviorInspector.DrawInspectorGUI(behaviorSource, false, ref showVariables);
                    }
                }
                else
                {
                    GUILayout.Space(5f);
                    GUILayout.Label("No behavior tree selected. Create a new behavior tree or select one from the hierarchy.",
                        BehaviorDesignerUtility.LabelWrapGUIStyle,
                        new GUILayoutOption[1]
                        {
                            GUILayout.Width(285f)
                        });
                }
            }
            else if (this.mBehaviorToolbarSelection == 1)
            {
                this.mTaskList.DrawTaskList(this, !this.ViewOnlyMode());
                if (toolbarSelection != 1)
                    this.mTaskList.FocusSearchField(false, false);
            }
            else if (this.mBehaviorToolbarSelection == 2)
            {
                if (this.mActiveBehaviorSource != null)
                {
                    if (this.mVariableInspector.DrawVariables(this.mExternalParent == null ? this.mActiveBehaviorSource : this.mExternalParent))
                        this.SaveBehavior();
                    if (toolbarSelection != 2)
                        this.mVariableInspector.FocusNameField();
                }
                else
                {
                    GUILayout.Space(5f);
                    GUILayout.Label("No behavior tree selected. Create a new behavior tree or select one from the hierarchy.",
                        BehaviorDesignerUtility.LabelWrapGUIStyle,
                        new GUILayoutOption[1]
                        {
                            GUILayout.Width(285f)
                        });
                }
            }
            else if (this.mBehaviorToolbarSelection == 3)
            {
                if (this.mGraphDesigner.SelectedNodes.Count == 1 && !this.mGraphDesigner.SelectedNodes[0].IsEntryDisplay)
                {
                    Task task = this.mGraphDesigner.SelectedNodes[0].Task;
                    if (this.mNodeDesignerTaskMap != null && this.mNodeDesignerTaskMap.Count > 0)
                    {
                        NodeDesigner nodeDesigner = this.mGraphDesigner.SelectedNodes[0].Task.NodeData.NodeDesigner as NodeDesigner;
                        if ((UnityEngine.Object)nodeDesigner != (UnityEngine.Object)null && this.mNodeDesignerTaskMap.ContainsKey(nodeDesigner))
                            task = this.mNodeDesignerTaskMap[nodeDesigner];
                    }

                    if (this.mTaskInspector.DrawTaskInspector(this.mActiveBehaviorSource, this.mTaskList, task, !this.ViewOnlyMode()) && (!EditorApplication.isPlaying || (bool)(UnityEngine.Object)(this.mActiveObject as ExternalBehavior)))
                        this.SaveBehavior();
                }
                else
                {
                    GUILayout.Space(5f);
                    if (this.mGraphDesigner.SelectedNodes.Count > 1)
                        GUILayout.Label("Only one task can be selected at a time to\n view its properties.",
                            BehaviorDesignerUtility.LabelWrapGUIStyle,
                            new GUILayoutOption[1]
                            {
                                GUILayout.Width(285f)
                            });
                    else
                        GUILayout.Label("Select a task from the tree to\nview its properties.",
                            BehaviorDesignerUtility.LabelWrapGUIStyle,
                            new GUILayoutOption[1]
                            {
                                GUILayout.Width(285f)
                            });
                }
            }

            GUILayout.EndArea();
        }

        private bool DrawGraphArea()
        {
            if (Event.current.type != EventType.ScrollWheel && !this.mTakingScreenshot)
            {
                Vector2 vector2 = GUI.BeginScrollView(new Rect(this.mGraphRect.x, this.mGraphRect.y, this.mGraphRect.width + 15f, this.mGraphRect.height + 15f), this.mGraphScrollPosition, new Rect(0.0f, 0.0f, this.mGraphScrollSize.x, this.mGraphScrollSize.y), true, true);
                if (vector2 != this.mGraphScrollPosition && Event.current.type != EventType.DragUpdated && Event.current.type != EventType.Ignore)
                {
                    this.mGraphOffset -= (vector2 - this.mGraphScrollPosition) / this.mGraphZoom;
                    this.mGraphScrollPosition = vector2;
                    this.mGraphDesigner.GraphDirty();
                }

                GUI.EndScrollView();
            }

            GUI.Box(this.mGraphRect, string.Empty, BehaviorDesignerUtility.GraphBackgroundGUIStyle);
            this.DrawGrid();
            EditorZoomArea.Begin(this.mGraphRect, this.mGraphZoom);
            Vector2 mousePosition;
            if (!this.GetMousePositionInGraph(out mousePosition))
                mousePosition = new Vector2(-1f, -1f);
            bool flag = false;
            if ((UnityEngine.Object)this.mGraphDesigner != (UnityEngine.Object)null && this.mGraphDesigner.DrawNodes(mousePosition, this.mGraphOffset))
                flag = true;
            if (this.mTakingScreenshot && Event.current.type == EventType.Repaint)
                this.RenderScreenshotTile();
            if (this.mIsSelecting)
                GUI.Box(this.GetSelectionArea(), string.Empty, BehaviorDesignerUtility.SelectionGUIStyle);
            EditorZoomArea.End();
            this.DrawGraphStatus();
            this.DrawSelectedTaskDescription();
            return flag;
        }

        private void DrawGrid()
        {
            if (!BehaviorDesignerPreferences.GetBool(BDPreferences.SnapToGrid) || Event.current.type != EventType.Repaint)
                return;
            this.mGridMaterial.SetPass(!EditorGUIUtility.isProSkin ? 1 : 0);
            GL.PushMatrix();
            GL.Begin(1);
            this.DrawGridLines(10f * this.mGraphZoom, new Vector2(this.mGraphOffset.x % 10f * this.mGraphZoom, this.mGraphOffset.y % 10f * this.mGraphZoom));
            GL.End();
            GL.PopMatrix();
            this.mGridMaterial.SetPass(!EditorGUIUtility.isProSkin ? 3 : 2);
            GL.PushMatrix();
            GL.Begin(1);
            this.DrawGridLines(50f * this.mGraphZoom, new Vector2(this.mGraphOffset.x % 50f * this.mGraphZoom, this.mGraphOffset.y % 50f * this.mGraphZoom));
            GL.End();
            GL.PopMatrix();
        }

        private void DrawGridLines(float gridSize, Vector2 offset)
        {
            float num1 = this.mGraphRect.x + offset.x;
            if ((double)offset.x < 0.0)
                num1 += gridSize;
            for (float x = num1; (double)x < (double)this.mGraphRect.x + (double)this.mGraphRect.width; x += gridSize)
                this.DrawLine(new Vector2(x, this.mGraphRect.y), new Vector2(x, this.mGraphRect.y + this.mGraphRect.height));
            float num2 = this.mGraphRect.y + offset.y;
            if ((double)offset.y < 0.0)
                num2 += gridSize;
            for (float y = num2; (double)y < (double)this.mGraphRect.y + (double)this.mGraphRect.height; y += gridSize)
                this.DrawLine(new Vector2(this.mGraphRect.x, y), new Vector2(this.mGraphRect.x + this.mGraphRect.width, y));
        }

        private void DrawLine(Vector2 p1, Vector2 p2)
        {
            GL.Vertex((Vector3)p1);
            GL.Vertex((Vector3)p2);
        }

        private void DrawGraphStatus()
        {
            if (this.mGraphStatus.Equals(string.Empty))
                return;
            GUI.Label(new Rect(this.mGraphRect.x + 5f, this.mGraphRect.y + 5f, this.mGraphRect.width, 30f), this.mGraphStatus, BehaviorDesignerUtility.GraphStatusGUIStyle);
        }

        private void DrawSelectedTaskDescription()
        {
            TaskDescriptionAttribute[] customAttributes;
            if (!BehaviorDesignerPreferences.GetBool(BDPreferences.ShowTaskDescription) || this.mGraphDesigner.SelectedNodes.Count != 1 ||
                (customAttributes = ((object)this.mGraphDesigner.SelectedNodes[0].Task).GetType().GetCustomAttributes(typeof(TaskDescriptionAttribute), false) as TaskDescriptionAttribute[]).Length <= 0)
                return;
            float num1;
            float num2;
            BehaviorDesignerUtility.TaskCommentGUIStyle.CalcMinMaxWidth(new GUIContent(customAttributes[0].Description), out num1, out num2);
            float width = Mathf.Min(400f, num2 + 20f);
            float height = Mathf.Min(300f, BehaviorDesignerUtility.TaskCommentGUIStyle.CalcHeight(new GUIContent(customAttributes[0].Description), width)) + 3f;
            GUI.Box(new Rect(this.mGraphRect.x + 5f, (float)((double)this.mGraphRect.yMax - (double)height - 5.0), width, height), string.Empty, BehaviorDesignerUtility.TaskDescriptionGUIStyle);
            GUI.Box(new Rect(this.mGraphRect.x + 2f, (float)((double)this.mGraphRect.yMax - (double)height - 5.0), width, height), customAttributes[0].Description, BehaviorDesignerUtility.TaskCommentGUIStyle);
        }

        private void AddBehavior()
        {
            if (EditorApplication.isPlaying || !((UnityEngine.Object)Selection.activeGameObject != (UnityEngine.Object)null))
                return;
            GameObject activeGameObject = Selection.activeGameObject;
            this.mActiveObject = Selection.activeObject;
            this.mGraphDesigner = ScriptableObject.CreateInstance<GraphDesigner>();
            System.Type typeWithinAssembly1 = TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.BehaviorTree");
            Behavior behavior = BehaviorUndo.AddComponent(activeGameObject, typeWithinAssembly1) as Behavior;
            Behavior[] components = activeGameObject.GetComponents<Behavior>();
            HashSet<string> stringSet = new HashSet<string>();
            string empty = string.Empty;
            for (int index = 0; index < components.Length; ++index)
            {
                string str = components[index].GetBehaviorSource().behaviorName;
                int num = 2;
                while (stringSet.Contains(str))
                {
                    str = string.Format("{0} {1}", (object)components[index].GetBehaviorSource().behaviorName, (object)num);
                    ++num;
                }

                components[index].GetBehaviorSource().behaviorName = str;
                stringSet.Add(components[index].GetBehaviorSource().behaviorName);
            }

            this.LoadBehavior(behavior.GetBehaviorSource(), false);
            this.Repaint();
            if (!BehaviorDesignerPreferences.GetBool(BDPreferences.AddGameGUIComponent))
                return;
            System.Type typeWithinAssembly2 = TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.BehaviorGameGUI");
            BehaviorUndo.AddComponent(activeGameObject, typeWithinAssembly2);
        }

        private void RemoveBehavior()
        {
            if (EditorApplication.isPlaying || !((UnityEngine.Object)(this.mActiveObject as GameObject) != (UnityEngine.Object)null) ||
                this.mActiveBehaviorSource.EntryTask != null && (this.mActiveBehaviorSource.EntryTask == null || !EditorUtility.DisplayDialog("Remove Behavior Tree", "Are you sure you want to remove this behavior tree?", "Yes", "No")))
                return;
            GameObject mActiveObject = this.mActiveObject as GameObject;
            int num = this.IndexForBehavior(this.mActiveBehaviorSource.Owner);
            BehaviorUndo.DestroyObject(this.mActiveBehaviorSource.Owner.GetObject(), true);
            int index = num - 1;
            if (index == -1 && mActiveObject.GetComponents<Behavior>().Length > 0)
                index = 0;
            if (index > -1)
                this.LoadBehavior(mActiveObject.GetComponents<Behavior>()[index].GetBehaviorSource(), true);
            else
                this.ClearGraph();
            this.ClearBreadcrumbMenu();
            this.Repaint();
        }

        private int IndexForBehavior(IBehavior behavior)
        {
            if (!(bool)(UnityEngine.Object)(behavior.GetObject() as Behavior))
                return 0;
            Behavior[] components = ((Component)(behavior.GetObject() as Behavior)).gameObject.GetComponents<Behavior>();
            for (int index = 0; index < components.Length; ++index)
            {
                if (((object)components[index]).Equals((object)behavior))
                    return index;
            }

            return -1;
        }

        public NodeDesigner AddTask(System.Type type, bool useMousePosition)
        {
            if ((UnityEngine.Object)(this.mActiveObject as GameObject) == (UnityEngine.Object)null && (UnityEngine.Object)(this.mActiveObject as ExternalBehavior) == (UnityEngine.Object)null || EditorApplication.isPlaying && !(bool)(UnityEngine.Object)(this.mActiveObject as ExternalBehavior))
                return (NodeDesigner)null;
            Vector2 mousePosition = new Vector2(this.mGraphRect.width / (2f * this.mGraphZoom), 150f);
            if (useMousePosition)
            {
                if (this.mShowQuickTaskList)
                    mousePosition = (this.mQuickTaskListRect.position - new Vector2(this.mQuickTaskListRect.width, 0.0f)) / this.mGraphZoom;
                else
                    this.GetMousePositionInGraph(out mousePosition);
            }

            mousePosition -= this.mGraphOffset;
            this.mShowQuickTaskList = false;
            GameObject mActiveObject = this.mActiveObject as GameObject;
            if ((UnityEngine.Object)mActiveObject != (UnityEngine.Object)null && (UnityEngine.Object)mActiveObject.GetComponent<Behavior>() == (UnityEngine.Object)null)
                this.AddBehavior();
            BehaviorUndo.RegisterUndo("Add", this.mActiveBehaviorSource.Owner.GetObject());
            NodeDesigner nodeDesigner;
            if (!((UnityEngine.Object)(nodeDesigner = this.mGraphDesigner.AddNode(this.mActiveBehaviorSource, type, mousePosition)) != (UnityEngine.Object)null))
                return (NodeDesigner)null;
            if (this.onAddTask != null)
                this.onAddTask(this.mActiveBehaviorSource, nodeDesigner.Task);
            this.SaveBehavior();
            return nodeDesigner;
        }

        public bool IsReferencingTasks() => this.mTaskInspector.ActiveReferenceTask != null;

        public bool IsReferencingField(FieldInfo fieldInfo) => fieldInfo.Equals((object)this.mTaskInspector.ActiveReferenceTaskFieldInfo);

        private void DisableReferenceTasks()
        {
            if (!this.IsReferencingTasks())
                return;
            this.ToggleReferenceTasks();
        }

        public void ToggleReferenceTasks() => this.ToggleReferenceTasks((Task)null, (FieldInfo)null);

        public void ToggleReferenceTasks(Task task, FieldInfo fieldInfo)
        {
            bool flag = !this.IsReferencingTasks();
            this.mTaskInspector.SetActiveReferencedTasks(!flag ? (Task)null : task, !flag ? (FieldInfo)null : fieldInfo);
            this.UpdateGraphStatus();
        }

        private void ReferenceTask(NodeDesigner nodeDesigner)
        {
            if (!((UnityEngine.Object)nodeDesigner != (UnityEngine.Object)null) || !this.mTaskInspector.ReferenceTasks(nodeDesigner.Task))
                return;
            this.SaveBehavior();
        }

        public void IdentifyNode(NodeDesigner nodeDesigner) => this.mGraphDesigner.IdentifyNode(nodeDesigner);

        private void TakeScreenshot()
        {
            this.mScreenshotPath = EditorUtility.SaveFilePanel("Save Screenshot", "Assets", this.mActiveBehaviorSource.behaviorName + "Screenshot.png", "png");
            if (this.mScreenshotPath.Length != 0 && Application.dataPath.Length < this.mScreenshotPath.Length)
            {
                this.mTakingScreenshot = true;
                this.mScreenshotGraphSize = this.mGraphDesigner.GraphSize((Vector3)this.mGraphOffset);
                this.mGraphDesigner.GraphDirty();
                if ((double)this.mScreenshotGraphSize.width == 0.0 || (double)this.mScreenshotGraphSize.height == 0.0)
                    this.mScreenshotGraphSize = new Rect(0.0f, 0.0f, 100f, 100f);
                this.mScreenshotStartGraphZoom = this.mGraphZoom;
                this.mScreenshotStartGraphOffset = this.mGraphOffset;
                this.mGraphZoom = 1f;
                this.mGraphOffset.x -= this.mScreenshotGraphSize.xMin - 10f;
                this.mGraphOffset.y -= this.mScreenshotGraphSize.yMin - 10f;
                this.mScreenshotGraphOffset = this.mGraphOffset;
                this.mScreenshotGraphSize.Set(this.mScreenshotGraphSize.xMin - 9f, this.mScreenshotGraphSize.yMin, this.mScreenshotGraphSize.width + 18f, this.mScreenshotGraphSize.height + 18f);
                this.mScreenshotTexture = new Texture2D((int)this.mScreenshotGraphSize.width, (int)this.mScreenshotGraphSize.height, TextureFormat.RGB24, false);
                this.Repaint();
            }
            else
            {
                if (!Path.GetExtension(this.mScreenshotPath).Equals(".png"))
                    return;
                Debug.LogError((object)"Error: Unable to save screenshot. The save location must be within the Asset directory.");
            }
        }

        private void RenderScreenshotTile()
        {
            float width = Mathf.Min(this.mGraphRect.width, this.mScreenshotGraphSize.width - (this.mGraphOffset.x - this.mScreenshotGraphOffset.x));
            float height = Mathf.Min(this.mGraphRect.height, this.mScreenshotGraphSize.height + (this.mGraphOffset.y - this.mScreenshotGraphOffset.y));
            this.mScreenshotTexture.ReadPixels(new Rect(this.mGraphRect.x, (float)(39.0 + (double)this.mGraphRect.height - (double)height - 7.0), width, height),
                -(int)((double)this.mGraphOffset.x - (double)this.mScreenshotGraphOffset.x),
                (int)((double)this.mScreenshotGraphSize.height - (double)height + ((double)this.mGraphOffset.y - (double)this.mScreenshotGraphOffset.y)));
            this.mScreenshotTexture.Apply(false);
            if ((double)this.mScreenshotGraphSize.xMin + (double)width - ((double)this.mGraphOffset.x - (double)this.mScreenshotGraphOffset.x) < (double)this.mScreenshotGraphSize.xMax)
            {
                this.mGraphOffset.x -= width - 1f;
                this.mGraphDesigner.GraphDirty();
                this.Repaint();
            }
            else if ((double)this.mScreenshotGraphSize.yMin + (double)height - ((double)this.mGraphOffset.y - (double)this.mScreenshotGraphOffset.y) < (double)this.mScreenshotGraphSize.yMax)
            {
                this.mGraphOffset.y -= height - 1f;
                this.mGraphOffset.x = this.mScreenshotGraphOffset.x;
                this.mGraphDesigner.GraphDirty();
                this.Repaint();
            }
            else
                this.SaveScreenshot();
        }

        private void SaveScreenshot()
        {
            byte[] png = ImageConversion.EncodeToPNG(this.mScreenshotTexture);
            UnityEngine.Object.DestroyImmediate((UnityEngine.Object)this.mScreenshotTexture, true);
            File.WriteAllBytes(this.mScreenshotPath, png);
            AssetDatabase.ImportAsset(string.Format("Assets/{0}", (object)this.mScreenshotPath.Substring(Application.dataPath.Length + 1)));
            this.mTakingScreenshot = false;
            this.mGraphZoom = this.mScreenshotStartGraphZoom;
            this.mGraphOffset = this.mScreenshotStartGraphOffset;
            this.mGraphDesigner.GraphDirty();
            this.Repaint();
        }

        private void HandleEvents()
        {
            if (this.mTakingScreenshot)
                return;
            if (Event.current.type != EventType.MouseUp && this.CheckForAutoScroll())
            {
                this.Repaint();
            }
            else
            {
                if (Event.current.type == EventType.Repaint || Event.current.type == EventType.Layout)
                    return;
                switch ((int)Event.current.type)
                {
                    case 0:
                        if (this.mShowQuickTaskList && !this.mQuickTaskListRect.Contains(this.mCurrentMousePosition))
                            this.mShowQuickTaskList = false;
                        if (Event.current.button == 0 && Event.current.modifiers != EventModifiers.Control)
                        {
                            Vector2 mousePosition;
                            if (this.GetMousePositionInGraph(out mousePosition))
                            {
                                if (!this.LeftMouseDown(Event.current.clickCount, mousePosition))
                                    break;
                                Event.current.Use();
                                break;
                            }

                            if (!this.GetMousePositionInPropertiesPane(out mousePosition) || this.mBehaviorToolbarSelection != 2 || !this.mVariableInspector.LeftMouseDown((IVariableSource)this.mActiveBehaviorSource, this.mActiveBehaviorSource, mousePosition))
                                break;
                            Event.current.Use();
                            this.Repaint();
                            break;
                        }

                        if (Event.current.button != 1 && (Event.current.modifiers != EventModifiers.Control || Event.current.button != 0) || !this.RightMouseDown())
                            break;
                        Event.current.Use();
                        break;
                    case 1:
                        if (Event.current.button == 0 && Event.current.modifiers != EventModifiers.Control)
                        {
                            if (!this.LeftMouseRelease())
                                break;
                            Event.current.Use();
                            break;
                        }

                        if (Event.current.button != 1 && (Event.current.modifiers != EventModifiers.Control || Event.current.button != 0) || !this.mShowRightClickMenu)
                            break;
                        this.mShowRightClickMenu = false;
                        this.mRightClickMenu.ShowAsContext();
                        Event.current.Use();
                        break;
                    case 2:
                        if (!this.MouseMove())
                            break;
                        Event.current.Use();
                        break;
                    case 3:
                        if (Event.current.button == 0)
                        {
                            if (this.LeftMouseDragged())
                            {
                                Event.current.Use();
                                break;
                            }

                            if (Event.current.modifiers != EventModifiers.Alt || !this.MousePan())
                                break;
                            Event.current.Use();
                            break;
                        }

                        if (Event.current.button != 2 || !this.MousePan())
                            break;
                        Event.current.Use();
                        break;
                    case 4:
                        if (Event.current.keyCode != KeyCode.LeftMeta && Event.current.keyCode != KeyCode.RightMeta)
                            break;
                        this.mCommandDown = true;
                        break;
                    case 5:
                        if (Event.current.keyCode == KeyCode.Delete || Event.current.keyCode == KeyCode.Backspace || Event.current.commandName.Equals("Delete"))
                        {
                            if (this.PropertiesInspectorHasFocus() || EditorApplication.isPlaying && !(bool)(UnityEngine.Object)(this.mActiveObject as ExternalBehavior))
                                break;
                            this.DeleteNodes();
                            Event.current.Use();
                            break;
                        }

                        if (Event.current.keyCode == (KeyCode)BehaviorDesignerPreferences.GetInt(BDPreferences.QuickSearchKeyCode) && Event.current.modifiers == null)
                        {
                            Vector2 mousePosition;
                            if (this.mShowQuickTaskList || !this.GetMousePositionInGraph(out mousePosition))
                                break;
                            this.mShowQuickTaskList = true;
                            this.mQuickTaskListRect = new Rect(mousePosition * this.mGraphZoom + new Vector2(200f, 0.0f) * 1.5f, new Vector2(200f, 200f));
                            if ((double)this.mQuickTaskListRect.xMax > (double)this.mGraphRect.xMax)
                                this.mQuickTaskListRect.x -= this.mQuickTaskListRect.xMax - this.mGraphRect.xMax;
                            if ((double)this.mQuickTaskListRect.yMax > (double)this.mGraphRect.yMax)
                                this.mQuickTaskListRect.y -= this.mQuickTaskListRect.yMax - this.mGraphRect.yMax;
                            if ((double)this.mQuickTaskListRect.yMin < (double)this.mGraphRect.yMin)
                                this.mQuickTaskListRect.y += this.mGraphRect.yMin - this.mQuickTaskListRect.yMin;
                            this.mTaskList.FocusSearchField(true, true);
                            Event.current.Use();
                            this.Repaint();
                            break;
                        }

                        if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                        {
                            if (this.mBehaviorToolbarSelection == 2 && this.mVariableInspector.HasFocus())
                            {
                                if (this.mVariableInspector.ClearFocus(true, this.mActiveBehaviorSource))
                                    this.SaveBehavior();
                                this.Repaint();
                            }
                            else
                            {
                                this.DisableReferenceTasks();
                                if (this.mShowQuickTaskList)
                                {
                                    this.mTaskList.SelectQuickTask(this);
                                    this.Repaint();
                                }
                            }

                            Event.current.Use();
                            break;
                        }

                        if (Event.current.keyCode == KeyCode.Escape)
                        {
                            this.DisableReferenceTasks();
                            if (!this.mShowQuickTaskList)
                                break;
                            this.mShowQuickTaskList = false;
                            Event.current.Use();
                            this.Repaint();
                            break;
                        }

                        if (Event.current.keyCode == KeyCode.UpArrow || Event.current.keyCode == KeyCode.DownArrow)
                        {
                            if (!this.mShowQuickTaskList)
                                break;
                            this.mTaskList.MoveSelectedQuickTask(Event.current.keyCode == KeyCode.DownArrow);
                            Event.current.Use();
                            this.Repaint();
                            break;
                        }

                        if (Event.current.keyCode == KeyCode.A && Event.current.modifiers == EventModifiers.Control)
                        {
                            if (this.mShowQuickTaskList)
                            {
                                this.mTaskList.FocusSearchField(true, false);
                                break;
                            }

                            if (this.mBehaviorToolbarSelection != 1 || GUIUtility.keyboardControl == 0)
                                break;
                            this.mTaskList.FocusSearchField(false, false);
                            break;
                        }

                        if (Event.current.keyCode != KeyCode.LeftMeta && Event.current.keyCode != KeyCode.RightMeta)
                            break;
                        this.mCommandDown = false;
                        break;
                    case 6:
                        if (BehaviorDesignerPreferences.GetBool(BDPreferences.MouseWhellScrolls) && !this.mCommandDown)
                        {
                            this.MousePan();
                            break;
                        }

                        if (!this.MouseZoom())
                            break;
                        Event.current.Use();
                        break;
                    case 13:
                        if (EditorApplication.isPlaying && !(bool)(UnityEngine.Object)(this.mActiveObject as ExternalBehavior) ||
                            !Event.current.commandName.Equals("Copy") && !Event.current.commandName.Equals("Paste") && !Event.current.commandName.Equals("Cut") && !Event.current.commandName.Equals("SelectAll") && !Event.current.commandName.Equals("Duplicate") || this.PropertiesInspectorHasFocus() ||
                            this.ViewOnlyMode())
                            break;
                        Event.current.Use();
                        break;
                    case 14:
                        if (this.PropertiesInspectorHasFocus() || EditorApplication.isPlaying && !(bool)(UnityEngine.Object)(this.mActiveObject as ExternalBehavior) || this.ViewOnlyMode())
                            break;
                        if (Event.current.commandName.Equals("Copy"))
                        {
                            this.CopyNodes();
                            Event.current.Use();
                            break;
                        }

                        if (Event.current.commandName.Equals("Paste"))
                        {
                            this.PasteNodes();
                            Event.current.Use();
                            break;
                        }

                        if (Event.current.commandName.Equals("Cut"))
                        {
                            this.CutNodes();
                            Event.current.Use();
                            break;
                        }

                        if (Event.current.commandName.Equals("SelectAll"))
                        {
                            this.mGraphDesigner.SelectAll();
                            Event.current.Use();
                            break;
                        }

                        if (!Event.current.commandName.Equals("Duplicate"))
                            break;
                        this.DuplicateNodes();
                        Event.current.Use();
                        break;
                }
            }
        }

        private bool CheckForAutoScroll()
        {
            if (!this.GetMousePositionInGraph(out Vector2 _) || this.mGraphScrollRect.Contains(this.mCurrentMousePosition) || !this.mIsDragging && !this.mIsSelecting && !((UnityEngine.Object)this.mGraphDesigner.ActiveNodeConnection != (UnityEngine.Object)null))
                return false;
            Vector2 zero = Vector2.zero;
            if ((double)this.mCurrentMousePosition.y < (double)this.mGraphScrollRect.yMin + 15.0)
                zero.y = 3f;
            else if ((double)this.mCurrentMousePosition.y > (double)this.mGraphScrollRect.yMax - 15.0)
                zero.y = -3f;
            if ((double)this.mCurrentMousePosition.x < (double)this.mGraphScrollRect.xMin + 15.0)
                zero.x = 3f;
            else if ((double)this.mCurrentMousePosition.x > (double)this.mGraphScrollRect.xMax - 15.0)
                zero.x = -3f;
            this.ScrollGraph(zero);
            if (this.mIsDragging)
                this.mGraphDesigner.DragSelectedNodes(-zero / this.mGraphZoom, Event.current.modifiers != EventModifiers.Alt);
            if (this.mIsSelecting)
                this.mSelectStartPosition += zero / this.mGraphZoom;
            return true;
        }

        private bool MouseMove()
        {
            Vector2 mousePosition;
            if (!this.GetMousePositionInGraph(out mousePosition))
                return false;
            NodeDesigner nodeDesigner = this.mGraphDesigner.NodeAt(mousePosition, this.mGraphOffset);
            if ((UnityEngine.Object)this.mGraphDesigner.HoverNode != (UnityEngine.Object)null &&
                ((UnityEngine.Object)nodeDesigner != (UnityEngine.Object)null && !((object)this.mGraphDesigner.HoverNode).Equals((object)nodeDesigner) || !this.mGraphDesigner.HoverNode.HoverBarAreaContains(mousePosition, this.mGraphOffset)))
            {
                this.mGraphDesigner.ClearHover();
                this.Repaint();
            }

            if ((bool)(UnityEngine.Object)nodeDesigner && !nodeDesigner.IsEntryDisplay && !this.ViewOnlyMode())
                this.mGraphDesigner.Hover(nodeDesigner);
            return (UnityEngine.Object)this.mGraphDesigner.HoverNode != (UnityEngine.Object)null;
        }

        private bool LeftMouseDown(int clickCount, Vector2 mousePosition)
        {
            if (this.PropertiesInspectorHasFocus())
            {
                this.mTaskInspector.ClearFocus();
                this.mVariableInspector.ClearFocus(false, (BehaviorSource)null);
                this.Repaint();
            }

            NodeDesigner nodeDesigner = this.mGraphDesigner.NodeAt(mousePosition, this.mGraphOffset);
            if (Event.current.modifiers == EventModifiers.Alt)
            {
                this.mNodeClicked = this.mGraphDesigner.IsSelected(nodeDesigner);
                return false;
            }

            if (this.IsReferencingTasks())
            {
                if ((UnityEngine.Object)nodeDesigner == (UnityEngine.Object)null)
                    this.DisableReferenceTasks();
                else
                    this.ReferenceTask(nodeDesigner);
                return true;
            }

            if ((UnityEngine.Object)nodeDesigner != (UnityEngine.Object)null)
            {
                if ((UnityEngine.Object)this.mGraphDesigner.HoverNode != (UnityEngine.Object)null && !((object)nodeDesigner).Equals((object)this.mGraphDesigner.HoverNode))
                {
                    this.mGraphDesigner.ClearHover();
                    this.mGraphDesigner.Hover(nodeDesigner);
                }

                NodeConnection connection;
                if (!this.ViewOnlyMode() && (UnityEngine.Object)(connection = nodeDesigner.NodeConnectionRectContains(mousePosition, this.mGraphOffset)) != (UnityEngine.Object)null)
                {
                    if (this.mGraphDesigner.NodeCanOriginateConnection(nodeDesigner, connection))
                        this.mGraphDesigner.ActiveNodeConnection = connection;
                    return true;
                }

                if (nodeDesigner.Contains(mousePosition, this.mGraphOffset, false))
                {
                    this.mKeepTasksSelected = false;
                    if (this.mGraphDesigner.IsSelected(nodeDesigner))
                    {
                        if (Event.current.modifiers == EventModifiers.Control)
                        {
                            this.mKeepTasksSelected = true;
                            this.mGraphDesigner.Deselect(nodeDesigner);
                        }
                        else if (Event.current.modifiers == EventModifiers.Shift && nodeDesigner.Task is ParentTask)
                        {
                            nodeDesigner.Task.NodeData.Collapsed = !nodeDesigner.Task.NodeData.Collapsed;
                            this.mGraphDesigner.DeselectWithParent(nodeDesigner);
                        }
                        else if (clickCount == 2)
                        {
                            if (this.mBehaviorToolbarSelection != 3 && BehaviorDesignerPreferences.GetBool(BDPreferences.OpenInspectorOnTaskDoubleClick))
                                this.mBehaviorToolbarSelection = 3;
                            else if (nodeDesigner.Task is BehaviorReference)
                            {
                                BehaviorReference task = nodeDesigner.Task as BehaviorReference;
                                if (task.GetExternalBehaviors() != null && task.GetExternalBehaviors().Length > 0 && (UnityEngine.Object)task.GetExternalBehaviors()[0] != (UnityEngine.Object)null)
                                {
                                    if (this.mLockActiveGameObject)
                                        this.LoadBehavior(task.GetExternalBehaviors()[0].GetBehaviorSource(), false);
                                    else
                                        Selection.activeObject = (UnityEngine.Object)task.GetExternalBehaviors()[0];
                                }
                            }
                        }
                    }
                    else
                    {
                        if (Event.current.modifiers != EventModifiers.Shift && Event.current.modifiers != EventModifiers.Control)
                        {
                            this.mGraphDesigner.ClearNodeSelection();
                            this.mGraphDesigner.ClearConnectionSelection();
                            if (BehaviorDesignerPreferences.GetBool(BDPreferences.OpenInspectorOnTaskSelection))
                                this.mBehaviorToolbarSelection = 3;
                        }
                        else
                            this.mKeepTasksSelected = true;

                        this.mGraphDesigner.Select(nodeDesigner);
                    }

                    this.mNodeClicked = this.mGraphDesigner.IsSelected(nodeDesigner);
                    return true;
                }
            }

            if ((UnityEngine.Object)this.mGraphDesigner.HoverNode != (UnityEngine.Object)null)
            {
                bool collapsedButtonClicked = false;
                if (this.mGraphDesigner.HoverNode.HoverBarButtonClick(mousePosition, this.mGraphOffset, ref collapsedButtonClicked))
                {
                    this.SaveBehavior();
                    if (collapsedButtonClicked && this.mGraphDesigner.HoverNode.Task.NodeData.Collapsed)
                        this.mGraphDesigner.DeselectWithParent(this.mGraphDesigner.HoverNode);
                    return true;
                }
            }

            List<NodeConnection> nodeConnections = new List<NodeConnection>();
            this.mGraphDesigner.NodeConnectionsAt(mousePosition, this.mGraphOffset, ref nodeConnections);
            if (nodeConnections.Count > 0)
            {
                if (Event.current.modifiers != EventModifiers.Shift && Event.current.modifiers != EventModifiers.Control)
                {
                    this.mGraphDesigner.ClearNodeSelection();
                    this.mGraphDesigner.ClearConnectionSelection();
                }

                for (int index = 0; index < nodeConnections.Count; ++index)
                {
                    if (this.mGraphDesigner.IsSelected(nodeConnections[index]))
                    {
                        if (Event.current.modifiers == EventModifiers.Control)
                            this.mGraphDesigner.Deselect(nodeConnections[index]);
                    }
                    else
                        this.mGraphDesigner.Select(nodeConnections[index]);
                }

                return true;
            }

            if (Event.current.modifiers != EventModifiers.Shift)
            {
                this.mGraphDesigner.ClearNodeSelection();
                this.mGraphDesigner.ClearConnectionSelection();
            }

            this.mSelectStartPosition = mousePosition;
            this.mIsSelecting = true;
            this.mIsDragging = false;
            this.mDragDelta = Vector2.zero;
            this.mNodeClicked = false;
            return true;
        }

        private bool LeftMouseDragged()
        {
            if (!this.GetMousePositionInGraph(out Vector2 _))
                return false;
            if (Event.current.modifiers != EventModifiers.Alt)
            {
                if (this.IsReferencingTasks())
                    return true;
                if (this.mIsSelecting)
                {
                    this.mGraphDesigner.DeselectAll((NodeDesigner)null);
                    List<NodeDesigner> nodeDesignerList = this.mGraphDesigner.NodesAt(this.GetSelectionArea(), this.mGraphOffset);
                    if (nodeDesignerList != null)
                    {
                        for (int index = 0; index < nodeDesignerList.Count; ++index)
                            this.mGraphDesigner.Select(nodeDesignerList[index]);
                    }

                    return true;
                }

                if ((UnityEngine.Object)this.mGraphDesigner.ActiveNodeConnection != (UnityEngine.Object)null)
                    return true;
            }

            if (!this.mNodeClicked || this.ViewOnlyMode())
                return false;
            Vector2 vector2 = Vector2.zero;
            if (BehaviorDesignerPreferences.GetBool(BDPreferences.SnapToGrid))
            {
                this.mDragDelta += Event.current.delta;
                if ((double)Mathf.Abs(this.mDragDelta.x) > 10.0)
                {
                    float num = Mathf.Abs(this.mDragDelta.x) % 10f;
                    vector2.x = (Mathf.Abs(this.mDragDelta.x) - num) * Mathf.Sign(this.mDragDelta.x);
                    this.mDragDelta.x = num * Mathf.Sign(this.mDragDelta.x);
                }

                if ((double)Mathf.Abs(this.mDragDelta.y) > 10.0)
                {
                    float num = Mathf.Abs(this.mDragDelta.y) % 10f;
                    vector2.y = (Mathf.Abs(this.mDragDelta.y) - num) * Mathf.Sign(this.mDragDelta.y);
                    this.mDragDelta.y = num * Mathf.Sign(this.mDragDelta.y);
                }
            }
            else
                vector2 = Event.current.delta;

            bool flag = this.mGraphDesigner.DragSelectedNodes(vector2 / this.mGraphZoom, Event.current.modifiers != EventModifiers.Alt);
            if (flag)
                this.mKeepTasksSelected = true;
            this.mIsDragging = true;
            return flag;
        }

        private bool LeftMouseRelease()
        {
            this.mNodeClicked = false;
            if (this.IsReferencingTasks())
            {
                if (!this.mTaskInspector.IsActiveTaskArray() && !this.mTaskInspector.IsActiveTaskNull())
                {
                    this.DisableReferenceTasks();
                    this.Repaint();
                }

                if (this.GetMousePositionInGraph(out Vector2 _))
                    return true;
                this.mGraphDesigner.ActiveNodeConnection = (NodeConnection)null;
                return false;
            }

            if (this.mIsSelecting)
            {
                this.mIsSelecting = false;
                return true;
            }

            if (this.mIsDragging)
            {
                BehaviorUndo.RegisterUndo("Drag", this.mActiveBehaviorSource.Owner.GetObject());
                this.SaveBehavior();
                this.mIsDragging = false;
                this.mDragDelta = (Vector2)Vector3.zero;
                return true;
            }

            if ((UnityEngine.Object)this.mGraphDesigner.ActiveNodeConnection != (UnityEngine.Object)null)
            {
                Vector2 mousePosition;
                if (!this.GetMousePositionInGraph(out mousePosition))
                {
                    this.mGraphDesigner.ActiveNodeConnection = (NodeConnection)null;
                    return false;
                }

                NodeDesigner nodeDesigner = this.mGraphDesigner.NodeAt(mousePosition, this.mGraphOffset);
                if ((UnityEngine.Object)nodeDesigner != (UnityEngine.Object)null && !((object)nodeDesigner).Equals((object)this.mGraphDesigner.ActiveNodeConnection.OriginatingNodeDesigner) && this.mGraphDesigner.NodeCanAcceptConnection(nodeDesigner, this.mGraphDesigner.ActiveNodeConnection))
                {
                    this.mGraphDesigner.ConnectNodes(this.mActiveBehaviorSource, nodeDesigner);
                    BehaviorUndo.RegisterUndo("Task Connection", this.mActiveBehaviorSource.Owner.GetObject());
                    this.SaveBehavior();
                }
                else
                    this.mGraphDesigner.ActiveNodeConnection = (NodeConnection)null;

                return true;
            }

            Vector2 mousePosition1;
            if (Event.current.modifiers == EventModifiers.Shift || this.mKeepTasksSelected || !this.GetMousePositionInGraph(out mousePosition1))
                return false;
            NodeDesigner nodeDesigner1 = this.mGraphDesigner.NodeAt(mousePosition1, this.mGraphOffset);
            if ((UnityEngine.Object)nodeDesigner1 != (UnityEngine.Object)null && !this.mGraphDesigner.IsSelected(nodeDesigner1))
                this.mGraphDesigner.DeselectAll(nodeDesigner1);
            return true;
        }

        private bool RightMouseDown()
        {
            if (this.IsReferencingTasks())
            {
                this.DisableReferenceTasks();
                return false;
            }

            Vector2 mousePosition;
            if (!this.GetMousePositionInGraph(out mousePosition))
                return false;
            NodeDesigner nodeDesigner = this.mGraphDesigner.NodeAt(mousePosition, this.mGraphOffset);
            if ((UnityEngine.Object)nodeDesigner == (UnityEngine.Object)null || !this.mGraphDesigner.IsSelected(nodeDesigner))
            {
                this.mGraphDesigner.ClearNodeSelection();
                this.mGraphDesigner.ClearConnectionSelection();
                if ((UnityEngine.Object)nodeDesigner != (UnityEngine.Object)null)
                    this.mGraphDesigner.Select(nodeDesigner);
            }

            if ((UnityEngine.Object)this.mGraphDesigner.HoverNode != (UnityEngine.Object)null)
                this.mGraphDesigner.ClearHover();
            this.BuildRightClickMenu(nodeDesigner);
            return true;
        }

        private bool MouseZoom()
        {
            Vector2 mousePosition1;
            if (!this.GetMousePositionInGraph(out mousePosition1))
                return false;
            this.mGraphZoom += (float)(-((double)Event.current.delta.y * (double)this.mGraphZoomMultiplier) / 150.0);
            this.mGraphZoom = Mathf.Clamp(this.mGraphZoom, 0.2f, 1f);
            Vector2 mousePosition2;
            this.GetMousePositionInGraph(out mousePosition2);
            this.mGraphOffset += mousePosition2 - mousePosition1;
            this.mGraphScrollPosition += mousePosition2 - mousePosition1;
            this.mGraphDesigner.GraphDirty();
            return true;
        }

        private bool MousePan()
        {
            if (!this.GetMousePositionInGraph(out Vector2 _))
                return false;
            Vector2 delta = Event.current.delta;
            if (Event.current.type == EventType.ScrollWheel)
            {
                delta *= -1.5f;
                if (Event.current.modifiers == EventModifiers.Control)
                {
                    delta.x = delta.y;
                    delta.y = 0.0f;
                }
            }

            this.ScrollGraph(delta);
            return true;
        }

        private void ScrollGraph(Vector2 amount)
        {
            this.mGraphOffset += amount / this.mGraphZoom;
            this.mGraphScrollPosition -= amount;
            this.mGraphDesigner.GraphDirty();
            this.Repaint();
        }

        private bool PropertiesInspectorHasFocus() => this.mTaskInspector.HasFocus() || this.mVariableInspector.HasFocus();

        private void AddTaskCallback(object obj) => this.AddTask((System.Type)obj, true);

        private void ReplaceTasksCallback(object obj)
        {
            if (!this.mGraphDesigner.ReplaceSelectedNodes(this.mActiveBehaviorSource, (System.Type)obj))
                return;
            this.SaveBehavior();
        }

        private void BehaviorSelectionCallback(object obj)
        {
            BehaviorSource behaviorSource = obj as BehaviorSource;
            this.mActiveObject = !(behaviorSource.Owner is Behavior) ? (UnityEngine.Object)(behaviorSource.Owner as ExternalBehavior) : (UnityEngine.Object)((Component)(behaviorSource.Owner as Behavior)).gameObject;
            if (!this.mLockActiveGameObject)
                Selection.activeObject = this.mActiveObject;
            this.LoadBehavior(behaviorSource, false);
            this.UpdateGraphStatus();
            if (!EditorApplication.isPaused)
                return;
            this.mUpdateNodeTaskMap = true;
            this.UpdateNodeTaskMap();
        }

        private void ToggleEnableState(object obj)
        {
            (obj as NodeDesigner).ToggleEnableState();
            this.SaveBehavior();
            this.Repaint();
        }

        private void ToggleCollapseState(object obj)
        {
            NodeDesigner nodeDesigner = obj as NodeDesigner;
            if (nodeDesigner.ToggleCollapseState())
                this.mGraphDesigner.DeselectWithParent(nodeDesigner);
            this.SaveBehavior();
            this.Repaint();
        }

        private void ToggleBreakpoint(object obj)
        {
            (obj as NodeDesigner).ToggleBreakpoint();
            this.SaveBehavior();
            this.Repaint();
        }

        private void OpenInFileEditor(object obj) => TaskInspector.OpenInFileEditor((object)(obj as NodeDesigner).Task);

        private void SelectInProject(object obj) => TaskInspector.SelectInProject((object)(obj as NodeDesigner).Task);

        private void CopyNodes() => this.mCopiedTasks = this.mGraphDesigner.Copy(this.mGraphOffset, this.mGraphZoom);

        private void PasteNodes()
        {
            if (this.mActiveObject == (UnityEngine.Object)null || EditorApplication.isPlaying && !(bool)(UnityEngine.Object)(this.mActiveObject as ExternalBehavior))
                return;
            GameObject mActiveObject = this.mActiveObject as GameObject;
            if ((UnityEngine.Object)mActiveObject != (UnityEngine.Object)null && (UnityEngine.Object)mActiveObject.GetComponent<Behavior>() == (UnityEngine.Object)null)
                this.AddBehavior();
            if (this.mCopiedTasks != null && this.mCopiedTasks.Count > 0)
                BehaviorUndo.RegisterUndo("Paste", this.mActiveBehaviorSource.Owner.GetObject());
            Vector2 mousePosition;
            if (!this.GetMousePositionInGraph(out mousePosition))
                mousePosition = new Vector2(float.MaxValue, float.MaxValue);
            this.mGraphDesigner.Paste(this.mActiveBehaviorSource, (Vector3)new Vector2(this.mGraphRect.width / (2f * this.mGraphZoom) - this.mGraphOffset.x, 150f - this.mGraphOffset.y), this.mCopiedTasks, this.mGraphOffset, this.mGraphZoom, mousePosition);
            this.SaveBehavior();
        }

        private void CutNodes()
        {
            this.mCopiedTasks = this.mGraphDesigner.Copy(this.mGraphOffset, this.mGraphZoom);
            if (this.mCopiedTasks != null && this.mCopiedTasks.Count > 0)
                BehaviorUndo.RegisterUndo("Cut", this.mActiveBehaviorSource.Owner.GetObject());
            this.mGraphDesigner.Delete(this.mActiveBehaviorSource, (BehaviorDesignerWindow.TaskCallbackHandler)null);
            this.SaveBehavior();
        }

        private void DuplicateNodes()
        {
            List<TaskSerializer> copiedTasks = this.mGraphDesigner.Copy(this.mGraphOffset, this.mGraphZoom);
            if (copiedTasks != null && copiedTasks.Count > 0)
                BehaviorUndo.RegisterUndo("Duplicate", this.mActiveBehaviorSource.Owner.GetObject());
            Vector2 mousePosition;
            if (!this.GetMousePositionInGraph(out mousePosition))
                mousePosition = new Vector2(float.MaxValue, float.MaxValue);
            this.mGraphDesigner.Paste(this.mActiveBehaviorSource, (Vector3)new Vector2(this.mGraphRect.width / (2f * this.mGraphZoom) - this.mGraphOffset.x, 150f - this.mGraphOffset.y), copiedTasks, this.mGraphOffset, this.mGraphZoom, mousePosition);
            this.SaveBehavior();
        }

        private void DeleteNodes()
        {
            if (this.ViewOnlyMode())
                return;
            this.mGraphDesigner.Delete(this.mActiveBehaviorSource, this.onRemoveTask);
            this.SaveBehavior();
        }

        public void RemoveSharedVariableReferences(SharedVariable sharedVariable)
        {
            if (!this.mGraphDesigner.RemoveSharedVariableReferences(sharedVariable))
                return;
            this.SaveBehavior();
            this.Repaint();
        }

        private void OnUndoRedo()
        {
            if (this.mActiveBehaviorSource == null)
                return;
            this.LoadBehavior(this.mActiveBehaviorSource, true, false);
        }

        private void SetupSizes()
        {
            float width = this.position.width;
            float num = this.position.height + 22f;
            if ((double)this.mPrevScreenWidth == (double)width && (double)this.mPrevScreenHeight == (double)num && this.mPropertiesPanelOnLeft == BehaviorDesignerPreferences.GetBool(BDPreferences.PropertiesPanelOnLeft))
                return;
            if (BehaviorDesignerPreferences.GetBool(BDPreferences.PropertiesPanelOnLeft))
            {
                this.mFileToolBarRect = new Rect(300f, 0.0f, width - 300f, 18f);
                this.mPropertyToolbarRect = new Rect(0.0f, 0.0f, 300f, 18f);
                this.mPropertyBoxRect = new Rect(0.0f, this.mPropertyToolbarRect.height, 300f, (float)((double)num - (double)this.mPropertyToolbarRect.height - 21.0));
                this.mGraphRect = new Rect(300f, 18f, (float)((double)width - 300.0 - 15.0), (float)((double)num - 36.0 - 21.0 - 15.0));
                this.mFindDialogueRect = new Rect((float)(300.0 + (double)this.mGraphRect.width - 300.0), (float)(18 + (!EditorGUIUtility.isProSkin ? 2 : 1)), 300f, 88f);
                this.mPreferencesPaneRect = new Rect((float)(300.0 + (double)this.mGraphRect.width - 290.0), (float)(18 + (!EditorGUIUtility.isProSkin ? 2 : 1)), 290f, 434f);
            }
            else
            {
                this.mFileToolBarRect = new Rect(0.0f, 0.0f, width - 300f, 18f);
                this.mPropertyToolbarRect = new Rect(width - 300f, 0.0f, 300f, 18f);
                this.mPropertyBoxRect = new Rect(width - 300f, this.mPropertyToolbarRect.height, 300f, (float)((double)num - (double)this.mPropertyToolbarRect.height - 21.0));
                this.mGraphRect = new Rect(0.0f, 18f, (float)((double)width - 300.0 - 15.0), (float)((double)num - 36.0 - 21.0 - 15.0));
                this.mFindDialogueRect = new Rect(this.mGraphRect.width - 300f, (float)(18 + (!EditorGUIUtility.isProSkin ? 2 : 1)), 300f, 88f);
                this.mPreferencesPaneRect = new Rect(this.mGraphRect.width - 290f, (float)(18 + (!EditorGUIUtility.isProSkin ? 2 : 1)), 290f, 434f);
            }

            this.mDebugToolBarRect = new Rect(this.mGraphRect.x, (float)((double)num - 18.0 - 21.0), this.mGraphRect.width + 15f, 18f);
            this.mGraphScrollRect.Set(this.mGraphRect.xMin + 15f, this.mGraphRect.yMin + 15f, this.mGraphRect.width - 30f, this.mGraphRect.height - 30f);
            if (this.mGraphScrollPosition == new Vector2(-1f, -1f))
                this.mGraphScrollPosition = (this.mGraphScrollSize - new Vector2(this.mGraphRect.width, this.mGraphRect.height)) / 2f - 2f * new Vector2(15f, 15f);
            this.mPrevScreenWidth = width;
            this.mPrevScreenHeight = num;
            this.mPropertiesPanelOnLeft = BehaviorDesignerPreferences.GetBool(BDPreferences.PropertiesPanelOnLeft);
        }

        private bool GetMousePositionInGraph(out Vector2 mousePosition)
        {
            mousePosition = this.mCurrentMousePosition;
            if (!this.mGraphRect.Contains(mousePosition) || this.mShowPrefPane && this.mPreferencesPaneRect.Contains(mousePosition) || this.mShowFindDialogue && this.mFindDialogueRect.Contains(mousePosition))
                return false;
            mousePosition -= new Vector2(this.mGraphRect.xMin, this.mGraphRect.yMin);
            mousePosition /= this.mGraphZoom;
            return true;
        }

        private bool GetMousePositionInPropertiesPane(out Vector2 mousePosition)
        {
            mousePosition = this.mCurrentMousePosition;
            if (!this.mPropertyBoxRect.Contains(mousePosition))
                return false;
            mousePosition.x -= this.mPropertyBoxRect.xMin;
            mousePosition.y -= this.mPropertyBoxRect.yMin;
            return true;
        }

        private Rect GetSelectionArea()
        {
            Vector2 mousePosition;
            if (this.GetMousePositionInGraph(out mousePosition))
            {
                float x = (double)this.mSelectStartPosition.x >= (double)mousePosition.x ? mousePosition.x : this.mSelectStartPosition.x;
                float num1 = (double)this.mSelectStartPosition.x <= (double)mousePosition.x ? mousePosition.x : this.mSelectStartPosition.x;
                float y = (double)this.mSelectStartPosition.y >= (double)mousePosition.y ? mousePosition.y : this.mSelectStartPosition.y;
                float num2 = (double)this.mSelectStartPosition.y <= (double)mousePosition.y ? mousePosition.y : this.mSelectStartPosition.y;
                this.mSelectionArea = new Rect(x, y, num1 - x, num2 - y);
            }

            return this.mSelectionArea;
        }

        public bool ViewOnlyMode() => !Application.isPlaying && this.mActiveBehaviorSource != null && this.mActiveBehaviorSource.Owner != null && !((object)this.mActiveBehaviorSource.Owner).Equals((object)null) &&
                                      (UnityEngine.Object)(this.mActiveBehaviorSource.Owner.GetObject() as Behavior) != (UnityEngine.Object)null && !BehaviorDesignerPreferences.GetBool(BDPreferences.EditablePrefabInstances) &&
                                      (PrefabUtility.GetPrefabAssetType(this.mActiveBehaviorSource.Owner.GetObject()) == PrefabAssetType.Regular || PrefabUtility.GetPrefabAssetType(this.mActiveBehaviorSource.Owner.GetObject()) == PrefabAssetType.Variant);

        private BehaviorSource BehaviorSourceFromIBehaviorHistory(IBehavior behavior)
        {
            if (behavior == null)
                return (BehaviorSource)null;
            if (!(behavior.GetObject() is GameObject))
                return behavior.GetBehaviorSource();
            Behavior[] components = (behavior.GetObject() as GameObject).GetComponents<Behavior>();
            for (int index = 0; index < ((IEnumerable<Behavior>)components).Count<Behavior>(); ++index)
            {
                if (components[index].GetBehaviorSource().BehaviorID == behavior.GetBehaviorSource().BehaviorID)
                    return components[index].GetBehaviorSource();
            }

            return (BehaviorSource)null;
        }

        public void SaveBehavior()
        {
            if (this.mActiveBehaviorSource == null || this.ViewOnlyMode() || EditorApplication.isPlaying && !(bool)(UnityEngine.Object)(this.mActiveObject as ExternalBehavior))
                return;
            this.mGraphDesigner.Save(this.mActiveBehaviorSource);
            this.CheckForErrors();
        }

        private void CheckForErrors()
        {
            if (this.mErrorDetails != null)
            {
                for (int index = 0; index < this.mErrorDetails.Count; ++index)
                {
                    if ((UnityEngine.Object)this.mErrorDetails[index].NodeDesigner != (UnityEngine.Object)null)
                        this.mErrorDetails[index].NodeDesigner.HasError = false;
                }
            }

            if (BehaviorDesignerPreferences.GetBool(BDPreferences.ErrorChecking))
            {
                this.mErrorDetails = ErrorCheck.CheckForErrors(this.mExternalParent == null ? this.mActiveBehaviorSource : this.mExternalParent);
                if (this.mErrorDetails != null)
                {
                    for (int index = 0; index < this.mErrorDetails.Count; ++index)
                    {
                        if (!((UnityEngine.Object)this.mErrorDetails[index].NodeDesigner == (UnityEngine.Object)null))
                            this.mErrorDetails[index].NodeDesigner.HasError = true;
                    }
                }
            }
            else
                this.mErrorDetails = (List<BehaviorDesigner.Editor.ErrorDetails>)null;

            if (!((UnityEngine.Object)ErrorWindow.instance != (UnityEngine.Object)null))
                return;
            ErrorWindow.instance.ErrorDetails = this.mErrorDetails;
            ErrorWindow.instance.Repaint();
        }

        public bool ContainsError(Task task, string fieldName)
        {
            if (this.mErrorDetails == null)
                return false;
            for (int index = 0; index < this.mErrorDetails.Count; ++index)
            {
                if (task == null)
                {
                    if (!((UnityEngine.Object)this.mErrorDetails[index].NodeDesigner != (UnityEngine.Object)null) && this.mErrorDetails[index].FieldName == fieldName)
                        return true;
                }
                else if (!((UnityEngine.Object)this.mErrorDetails[index].NodeDesigner == (UnityEngine.Object)null) && this.mErrorDetails[index].NodeDesigner.Task == task && this.mErrorDetails[index].FieldName == fieldName)
                    return true;
            }

            return false;
        }

        private bool UpdateCheck()
        {
            if (this.mUpdateCheckRequest != null && this.mUpdateCheckRequest.isDone)
            {
                if (!string.IsNullOrEmpty(this.mUpdateCheckRequest.error))
                {
                    this.mUpdateCheckRequest = (UnityWebRequest)null;
                    return false;
                }

                if (!"1.7.7".ToString().Equals(this.mUpdateCheckRequest.downloadHandler.text))
                    this.LatestVersion = this.mUpdateCheckRequest.downloadHandler.text;
                this.mUpdateCheckRequest = (UnityWebRequest)null;
            }

            if (BehaviorDesignerPreferences.GetBool(BDPreferences.UpdateCheck) && DateTime.Compare(this.LastUpdateCheck.AddDays(1.0), DateTime.UtcNow) < 0)
            {
                this.mUpdateCheckRequest = UnityWebRequest.Get(string.Format("https://opsive.com/asset/UpdateCheck.php?asset=BehaviorDesigner&version={0}&unityversion={1}&devplatform={2}&targetplatform={3}",
                    (object)"1.7.7",
                    (object)Application.unityVersion,
                    (object)Application.platform,
                    (object)EditorUserBuildSettings.activeBuildTarget));
                this.mUpdateCheckRequest.SendWebRequest();
                this.LastUpdateCheck = DateTime.UtcNow;
            }

            return this.mUpdateCheckRequest != null;
        }

        private void SaveAsAsset()
        {
            if (this.mActiveBehaviorSource == null)
                return;
            string path = EditorUtility.SaveFilePanel("Save Behavior Tree", "Assets", this.mActiveBehaviorSource.behaviorName + ".asset", "asset");
            if (path.Length != 0 && Application.dataPath.Length < path.Length)
            {
                System.Type typeWithinAssembly = TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.ExternalBehaviorTree");
                if (BehaviorDesignerPreferences.GetBool(BDPreferences.BinarySerialization))
                    BinarySerialization.Save(this.mActiveBehaviorSource);
                else
                    JSONSerialization.Save(this.mActiveBehaviorSource);
                ExternalBehavior instance = ScriptableObject.CreateInstance(typeWithinAssembly) as ExternalBehavior;
                instance.SetBehaviorSource(new BehaviorSource((IBehavior)instance)
                {
                    behaviorName = this.mActiveBehaviorSource.behaviorName,
                    behaviorDescription = this.mActiveBehaviorSource.behaviorDescription,
                    TaskData = this.mActiveBehaviorSource.TaskData
                });
                string str = string.Format("Assets/{0}", (object)path.Substring(Application.dataPath.Length + 1));
                AssetDatabase.DeleteAsset(str);
                AssetDatabase.CreateAsset((UnityEngine.Object)instance, str);
                AssetDatabase.ImportAsset(str);
                Selection.activeObject = (UnityEngine.Object)instance;
            }
            else
            {
                if (!Path.GetExtension(path).Equals(".asset"))
                    return;
                Debug.LogError((object)"Error: Unable to save external behavior tree. The save location must be within the Asset directory.");
            }
        }

        private void SaveAsPrefab()
        {
            if (this.mActiveBehaviorSource == null)
                return;
            string path = EditorUtility.SaveFilePanel("Save Behavior Tree", "Assets", this.mActiveBehaviorSource.behaviorName + ".prefab", "prefab");
            if (path.Length != 0 && Application.dataPath.Length < path.Length)
            {
                GameObject gameObject1 = new GameObject();
                System.Type type = System.Type.GetType("BehaviorDesigner.Runtime.BehaviorTree, Assembly-CSharp");
                if (type == (System.Type)null)
                    type = System.Type.GetType("BehaviorDesigner.Runtime.BehaviorTree, Assembly-CSharp-firstpass");
                Behavior behavior = gameObject1.AddComponent(type) as Behavior;
                behavior.SetBehaviorSource(new BehaviorSource((IBehavior)behavior)
                {
                    behaviorName = this.mActiveBehaviorSource.behaviorName,
                    behaviorDescription = this.mActiveBehaviorSource.behaviorDescription,
                    TaskData = this.mActiveBehaviorSource.TaskData
                });
                string str = string.Format("Assets/{0}", (object)path.Substring(Application.dataPath.Length + 1));
                AssetDatabase.DeleteAsset(str);
                GameObject gameObject2 = PrefabUtility.SaveAsPrefabAsset(gameObject1, str);
                UnityEngine.Object.DestroyImmediate((UnityEngine.Object)gameObject1, true);
                AssetDatabase.ImportAsset(str);
                Selection.activeObject = (UnityEngine.Object)gameObject2;
            }
            else
            {
                if (!Path.GetExtension(path).Equals(".prefab"))
                    return;
                Debug.LogError((object)"Error: Unable to save prefab. The save location must be within the Asset directory.");
            }
        }

        public void LoadBehavior(BehaviorSource behaviorSource, bool loadPrevBehavior) => this.LoadBehavior(behaviorSource, loadPrevBehavior, false);

        public void LoadBehavior(
            BehaviorSource behaviorSource,
            bool loadPrevBehavior,
            bool inspectorLoad)
        {
            if (behaviorSource == null || object.ReferenceEquals((object)behaviorSource.Owner, (object)null) || ((object)behaviorSource.Owner).Equals((object)null))
                return;
            if (inspectorLoad && !this.mSizesInitialized)
            {
                this.mActiveBehaviorID = behaviorSource.Owner.GetInstanceID();
                this.mPrevActiveObject = Selection.activeObject;
                this.mLoadedFromInspector = true;
            }
            else
            {
                if (!this.mSizesInitialized)
                    return;
                if (!loadPrevBehavior)
                {
                    this.DisableReferenceTasks();
                    this.mVariableInspector.ResetSelectedVariableIndex();
                }

                this.mExternalParent = (BehaviorSource)null;
                this.mActiveBehaviorSource = behaviorSource;
                if (behaviorSource.Owner is Behavior)
                {
                    this.mActiveObject = (UnityEngine.Object)((Component)(behaviorSource.Owner as Behavior)).gameObject;
                    ExternalBehavior externalBehavior = (behaviorSource.Owner as Behavior).ExternalBehavior;
                    if ((UnityEngine.Object)externalBehavior != (UnityEngine.Object)null && !EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        this.mActiveBehaviorSource = externalBehavior.BehaviorSource;
                        this.mActiveBehaviorSource.Owner = (IBehavior)externalBehavior;
                        this.mExternalParent = behaviorSource;
                        behaviorSource.CheckForSerialization(true, (BehaviorSource)null, false);
                        this.mActiveBehaviorSource.CheckForSerialization(true, (BehaviorSource)null, false);
                        if (VariableInspector.SyncVariables(behaviorSource, this.mActiveBehaviorSource.GetAllVariables()))
                        {
                            if (BehaviorDesignerPreferences.GetBool(BDPreferences.BinarySerialization))
                                BinarySerialization.Save(behaviorSource);
                            else
                                JSONSerialization.Save(behaviorSource);
                        }
                    }
                }
                else
                    this.mActiveObject = behaviorSource.Owner.GetObject();

                this.mActiveBehaviorSource.BehaviorID = this.mActiveBehaviorSource.Owner.GetInstanceID();
                this.mActiveBehaviorID = this.mActiveBehaviorSource.BehaviorID;
                this.mPrevActiveObject = Selection.activeObject;
                if (this.mBehaviorSourceHistory.Count == 0 || this.mBehaviorSourceHistoryIndex >= this.mBehaviorSourceHistory.Count || this.mBehaviorSourceHistory[this.mBehaviorSourceHistoryIndex] == (UnityEngine.Object)null ||
                    (this.mBehaviorSourceHistory[this.mBehaviorSourceHistoryIndex] as IBehavior).GetBehaviorSource() != null && !this.mActiveBehaviorSource.BehaviorID.Equals((this.mBehaviorSourceHistory[this.mBehaviorSourceHistoryIndex] as IBehavior).GetBehaviorSource().BehaviorID))
                {
                    for (int index = this.mBehaviorSourceHistory.Count - 1; index > this.mBehaviorSourceHistoryIndex; --index)
                        this.mBehaviorSourceHistory.RemoveAt(index);
                    this.mBehaviorSourceHistory.Add(this.mActiveBehaviorSource.Owner.GetObject());
                    ++this.mBehaviorSourceHistoryIndex;
                }

                Vector2 nodePosition = new Vector2(this.mGraphRect.width / (2f * this.mGraphZoom), 150f) - this.mGraphOffset;
                if (this.mGraphDesigner.Load(this.mActiveBehaviorSource, loadPrevBehavior && !this.mLoadedFromInspector, nodePosition) && this.mGraphDesigner.HasEntryNode() && (!loadPrevBehavior || this.mLoadedFromInspector))
                {
                    this.mGraphOffset = new Vector2(this.mGraphRect.width / (2f * this.mGraphZoom), 50f) - this.mGraphDesigner.EntryNodeOffset();
                    this.mGraphScrollPosition = (this.mGraphScrollSize - new Vector2(this.mGraphRect.width, this.mGraphRect.height)) / 2f - 2f * new Vector2(15f, 15f);
                }

                this.mLoadedFromInspector = false;
                if (!this.mLockActiveGameObject)
                    Selection.activeObject = this.mActiveObject;
                if (EditorApplication.isPlaying && this.mActiveBehaviorSource != null)
                {
                    this.mRightClickMenu = (GenericMenu)null;
                    this.mUpdateNodeTaskMap = true;
                    this.UpdateNodeTaskMap();
                }

                this.CheckForErrors();
                this.UpdateGraphStatus();
                this.ClearBreadcrumbMenu();
                this.Find();
                this.Repaint();
            }
        }

        public void ClearGraph()
        {
            this.mGraphDesigner.Clear(true);
            this.mActiveBehaviorSource = (BehaviorSource)null;
            this.CheckForErrors();
            this.UpdateGraphStatus();
            this.Repaint();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset() => BehaviorDesignerWindow.instance = (BehaviorDesignerWindow)null;

        private enum BreadcrumbMenuType
        {
            GameObjectBehavior,
            GameObject,
            Behavior,
        }

        public delegate void TaskCallbackHandler(BehaviorSource behaviorSource, Task task);
    }
}