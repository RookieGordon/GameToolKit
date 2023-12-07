// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.NodeDesigner
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
    [Serializable]
    public class NodeDesigner : ScriptableObject
    {
        [SerializeField] private Task mTask;
        [SerializeField] private bool mSelected;
        private int mIdentifyUpdateCount = -1;
        private bool mFoundTask;
        [SerializeField] private bool mConnectionIsDirty;
        private bool mRectIsDirty = true;
        private bool mIncomingRectIsDirty = true;
        private bool mOutgoingRectIsDirty = true;
        [SerializeField] private bool isParent;
        [SerializeField] private bool isEntryDisplay;
        [SerializeField] private bool showReferenceIcon;
        private bool showHoverBar;
        private bool hasError;
        [SerializeField] private string taskName = string.Empty;
        private Rect mRectangle;
        private Rect mOutgoingRectangle;
        private Rect mIncomingRectangle;
        private bool prevRunningState;
        private int prevCommentLength = -1;
        private List<int> prevWatchedFieldsLength = new List<int>();
        private int prevFriendlyNameLength = -1;
        [SerializeField] private NodeDesigner parentNodeDesigner;
        [SerializeField] private List<NodeConnection> outgoingNodeConnections;
        private bool mCacheIsDirty = true;
        private readonly Color grayColor = new Color(0.7f, 0.7f, 0.7f);
        private Rect nodeCollapsedTextureRect;
        private Rect iconTextureRect;
        private Rect titleRect;
        private Rect breakpointTextureRect;
        private Rect errorTextureRect;
        private Rect referenceTextureRect;
        private Rect conditionalAbortTextureRect;
        private Rect conditionalAbortLowerPriorityTextureRect;
        private Rect disabledButtonTextureRect;
        private Rect collapseButtonTextureRect;
        private Rect incomingConnectionTextureRect;
        private Rect outgoingConnectionTextureRect;
        private Rect successReevaluatingExecutionStatusTextureRect;
        private Rect successExecutionStatusTextureRect;
        private Rect failureExecutionStatusTextureRect;
        private Rect iconBorderTextureRect;
        private Rect watchedFieldRect;
        private Rect watchedFieldNamesRect;
        private Rect watchedFieldValuesRect;
        private Rect commentRect;
        private Rect commentLabelRect;

        public Task Task
        {
            get => this.mTask;
            set
            {
                this.mTask = value;
                this.Init();
            }
        }

        public void Select()
        {
            if (this.isEntryDisplay)
                return;
            this.mSelected = true;
        }

        public void Deselect() => this.mSelected = false;

        public void MarkDirty()
        {
            this.mConnectionIsDirty = true;
            this.mRectIsDirty = true;
            this.mIncomingRectIsDirty = true;
            this.mOutgoingRectIsDirty = true;
        }

        public bool IsParent => this.isParent;

        public bool IsEntryDisplay => this.isEntryDisplay;

        public bool ShowReferenceIcon
        {
            set => this.showReferenceIcon = value;
        }

        public bool ShowHoverBar
        {
            get => this.showHoverBar;
            set => this.showHoverBar = value;
        }

        public bool HasError
        {
            set => this.hasError = value;
        }

        public NodeDesigner ParentNodeDesigner
        {
            get => this.parentNodeDesigner;
            set => this.parentNodeDesigner = value;
        }

        public List<NodeConnection> OutgoingNodeConnections => this.outgoingNodeConnections;

        public Rect IncomingConnectionRect(Vector2 offset)
        {
            if (!this.mIncomingRectIsDirty)
                return this.mIncomingRectangle;
            Rect rect = this.Rectangle(offset, false, false);
            this.mIncomingRectangle = new Rect(rect.x + (float)(((double)rect.width - 42.0) / 2.0), rect.y - 14f, 42f, 14f);
            this.mIncomingRectIsDirty = false;
            return this.mIncomingRectangle;
        }

        public Rect OutgoingConnectionRect(Vector2 offset)
        {
            if (!this.mOutgoingRectIsDirty)
                return this.mOutgoingRectangle;
            Rect rect = this.Rectangle(offset, false, false);
            this.mOutgoingRectangle = new Rect(rect.x + (float)(((double)rect.width - 42.0) / 2.0), rect.yMax, 42f, 16f);
            this.mOutgoingRectIsDirty = false;
            return this.mOutgoingRectangle;
        }

        public void OnEnable() => this.hideFlags = HideFlags.HideAndDontSave;

        public void LoadTask(Task task, Behavior owner, ref int id)
        {
            if (task == null)
                return;
            this.mTask = task;
            if ((UnityEngine.Object)owner != (UnityEngine.Object)null)
                this.mTask.Owner = owner;
            Task mTask1 = this.mTask;
            int num1;
            id = (num1 = id) + 1;
            int num2 = num1;
            mTask1.ID = num2;
            this.mTask.NodeData.NodeDesigner = (object)this;
            this.mTask.NodeData.InitWatchedFields(this.mTask);
            if (!this.mTask.NodeData.FriendlyName.Equals(string.Empty))
            {
                this.mTask.FriendlyName = this.mTask.NodeData.FriendlyName;
                this.mTask.NodeData.FriendlyName = string.Empty;
            }

            this.LoadTaskIcon();
            this.Init();
            RequiredComponentAttribute[] customAttributes;
            if ((UnityEngine.Object)this.mTask.Owner != (UnityEngine.Object)null && (customAttributes = ((object)this.mTask).GetType().GetCustomAttributes(typeof(RequiredComponentAttribute), true) as RequiredComponentAttribute[]).Length > 0)
            {
                System.Type componentType = customAttributes[0].ComponentType;
                if (typeof(Component).IsAssignableFrom(componentType) && (UnityEngine.Object)((Component)this.mTask.Owner).gameObject.GetComponent(componentType) == (UnityEngine.Object)null)
                    ((Component)this.mTask.Owner).gameObject.AddComponent(componentType);
            }

            List<System.Type> baseClasses = FieldInspector.GetBaseClasses(((object)this.mTask).GetType());
            BindingFlags bindingAttr = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (int index1 = baseClasses.Count - 1; index1 > -1; --index1)
            {
                FieldInfo[] fields = baseClasses[index1].GetFields(bindingAttr);
                for (int index2 = 0; index2 < fields.Length; ++index2)
                {
                    if (typeof(SharedVariable).IsAssignableFrom(fields[index2].FieldType) && !fields[index2].FieldType.IsAbstract)
                    {
                        if (!(fields[index2].GetValue((object)this.mTask) is SharedVariable instance))
                            instance = Activator.CreateInstance(fields[index2].FieldType) as SharedVariable;
                        if (TaskUtility.HasAttribute(fields[index2], typeof(RequiredFieldAttribute)) || TaskUtility.HasAttribute(fields[index2], typeof(SharedRequiredAttribute)))
                            instance.IsShared = true;
                        fields[index2].SetValue((object)this.mTask, (object)instance);
                    }
                }
            }

            if (!this.isParent)
                return;
            ParentTask mTask2 = this.mTask as ParentTask;
            if (mTask2.Children != null)
            {
                for (int index = 0; index < mTask2.Children.Count; ++index)
                {
                    NodeDesigner instance1 = ScriptableObject.CreateInstance<NodeDesigner>();
                    instance1.LoadTask(mTask2.Children[index], owner, ref id);
                    NodeConnection instance2 = ScriptableObject.CreateInstance<NodeConnection>();
                    instance2.LoadConnection(this, NodeConnectionType.Fixed);
                    this.AddChildNode(instance1, instance2, true, true, index);
                }
            }

            this.mConnectionIsDirty = true;
        }

        public void LoadNode(Task task, BehaviorSource behaviorSource, Vector2 offset, ref int id)
        {
            this.mTask = task;
            this.mTask.Owner = behaviorSource.Owner as Behavior;
            Task mTask = this.mTask;
            int num1;
            id = (num1 = id) + 1;
            int num2 = num1;
            mTask.ID = num2;
            this.mTask.NodeData = new NodeData();
            this.mTask.NodeData.Offset = offset;
            this.mTask.NodeData.NodeDesigner = (object)this;
            this.LoadTaskIcon();
            this.Init();
            this.mTask.FriendlyName = this.taskName;
            RequiredComponentAttribute[] customAttributes;
            if ((UnityEngine.Object)this.mTask.Owner != (UnityEngine.Object)null && (customAttributes = ((object)this.mTask).GetType().GetCustomAttributes(typeof(RequiredComponentAttribute), true) as RequiredComponentAttribute[]).Length > 0)
            {
                System.Type componentType = customAttributes[0].ComponentType;
                if (typeof(Component).IsAssignableFrom(componentType) && (UnityEngine.Object)((Component)this.mTask.Owner).gameObject.GetComponent(componentType) == (UnityEngine.Object)null)
                    ((Component)this.mTask.Owner).gameObject.AddComponent(componentType);
            }

            List<System.Type> baseClasses = FieldInspector.GetBaseClasses(((object)this.mTask).GetType());
            BindingFlags bindingAttr = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (int index1 = baseClasses.Count - 1; index1 > -1; --index1)
            {
                FieldInfo[] fields = baseClasses[index1].GetFields(bindingAttr);
                for (int index2 = 0; index2 < fields.Length; ++index2)
                {
                    if (typeof(SharedVariable).IsAssignableFrom(fields[index2].FieldType) && !fields[index2].FieldType.IsAbstract)
                    {
                        if (!(fields[index2].GetValue((object)this.mTask) is SharedVariable instance))
                            instance = Activator.CreateInstance(fields[index2].FieldType) as SharedVariable;
                        if (TaskUtility.HasAttribute(fields[index2], typeof(RequiredFieldAttribute)) || TaskUtility.HasAttribute(fields[index2], typeof(SharedRequiredAttribute)))
                            instance.IsShared = true;
                        fields[index2].SetValue((object)this.mTask, (object)instance);
                    }
                }
            }
        }

        private void LoadTaskIcon()
        {
            this.mTask.NodeData.Icon = (Texture)null;
            TaskIconAttribute[] customAttributes;
            if ((customAttributes = ((object)this.mTask).GetType().GetCustomAttributes(typeof(TaskIconAttribute), true) as TaskIconAttribute[]).Length > 0)
            {
                if (string.IsNullOrEmpty(customAttributes[0].IconPath))
                    customAttributes[0].IconPath = AssetDatabase.GUIDToAssetPath(!EditorGUIUtility.isProSkin ? customAttributes[0].LightIconGUID : customAttributes[0].DarkIconGUID);
                this.mTask.NodeData.Icon = (Texture)BehaviorDesignerUtility.LoadIcon(customAttributes[0].IconPath);
            }

            if (!((UnityEngine.Object)this.mTask.NodeData.Icon == (UnityEngine.Object)null))
                return;
            string empty = string.Empty;
            this.mTask.NodeData.Icon = (Texture)BehaviorDesignerUtility.LoadIcon(!((object)this.mTask).GetType().IsSubclassOf(typeof(BehaviorDesigner.Runtime.Tasks.Action))
                ? (!((object)this.mTask).GetType().IsSubclassOf(typeof(Conditional))
                    ? (!((object)this.mTask).GetType().IsSubclassOf(typeof(Composite)) ? (!((object)this.mTask).GetType().IsSubclassOf(typeof(Decorator)) ? "{SkinColor}EntryIcon.png" : "{SkinColor}DecoratorIcon.png") : "{SkinColor}CompositeIcon.png")
                    : "{SkinColor}ConditionalIcon.png")
                : "{SkinColor}ActionIcon.png");
        }

        private void Init()
        {
            this.taskName = BehaviorDesignerUtility.SplitCamelCase(((object)this.mTask).GetType().Name.ToString());
            this.isParent = ((object)this.mTask).GetType().IsSubclassOf(typeof(ParentTask));
            if (this.isParent)
                this.outgoingNodeConnections = new List<NodeConnection>();
            this.mRectIsDirty = this.mCacheIsDirty = true;
            this.mIncomingRectIsDirty = true;
            this.mOutgoingRectIsDirty = true;
        }

        public void MakeEntryDisplay()
        {
            this.isEntryDisplay = this.isParent = true;
            this.mTask.FriendlyName = this.taskName = "Entry";
            this.outgoingNodeConnections = new List<NodeConnection>();
        }

        public Vector2 GetAbsolutePosition()
        {
            Vector2 offset = this.mTask.NodeData.Offset;
            if ((UnityEngine.Object)this.parentNodeDesigner != (UnityEngine.Object)null)
                offset += this.parentNodeDesigner.GetAbsolutePosition();
            if (BehaviorDesignerPreferences.GetBool(BDPreferences.SnapToGrid))
                offset.Set(BehaviorDesignerUtility.RoundToNearest(offset.x, 10f), BehaviorDesignerUtility.RoundToNearest(offset.y, 10f));
            return offset;
        }

        public Rect Rectangle(Vector2 offset, bool includeConnections, bool includeComments)
        {
            Rect rect = this.Rectangle(offset);
            if (includeConnections)
            {
                if (!this.isEntryDisplay)
                    rect.yMin -= 14f;
                if (this.isParent)
                    rect.yMax += 16f;
            }

            if (includeComments && this.mTask != null)
            {
                if (this.mTask.NodeData.WatchedFields != null && this.mTask.NodeData.WatchedFields.Count > 0 && (double)rect.xMax < (double)this.watchedFieldRect.xMax)
                    rect.xMax = this.watchedFieldRect.xMax;
                if (!this.GetNodeComment().Equals(string.Empty))
                {
                    if ((double)rect.xMax < (double)this.commentRect.xMax)
                        rect.xMax = this.commentRect.xMax;
                    if ((double)rect.yMax < (double)this.commentRect.yMax)
                        rect.yMax = this.commentRect.yMax;
                }
            }

            return rect;
        }

        private Rect Rectangle(Vector2 offset)
        {
            if (!this.mRectIsDirty)
                return this.mRectangle;
            this.mCacheIsDirty = true;
            if (this.mTask == null)
                return new Rect();
            float b = BehaviorDesignerUtility.TaskTitleGUIStyle.CalcSize(new GUIContent(((object)this).ToString())).x + 20f;
            if (!this.isParent)
            {
                float num1;
                float num2;
                BehaviorDesignerUtility.TaskCommentGUIStyle.CalcMinMaxWidth(new GUIContent(this.GetNodeComment()), out num1, out num2);
                num2 += 20f;
                b = (double)b <= (double)num2 ? num2 : b;
            }

            float width = Mathf.Min(220f, Mathf.Max(100f, b));
            Vector2 absolutePosition = this.GetAbsolutePosition();
            float height = (float)(20 + (!BehaviorDesignerPreferences.GetBool(BDPreferences.CompactMode) ? 52 : 22));
            this.mRectangle = new Rect((float)((double)absolutePosition.x + (double)offset.x - (double)width / 2.0), absolutePosition.y + offset.y, width, height);
            this.mRectIsDirty = false;
            return this.mRectangle;
        }

        private void UpdateCache(Rect nodeRect)
        {
            if (!this.mCacheIsDirty)
                return;
            this.nodeCollapsedTextureRect = new Rect((float)((double)nodeRect.x + ((double)nodeRect.width - 26.0) / 2.0 + 1.0), nodeRect.yMax + 2f, 26f, 6f);
            this.iconTextureRect = new Rect(nodeRect.x + (float)(((double)nodeRect.width - 44.0) / 2.0), (float)((double)nodeRect.y + 4.0 + 2.0), 44f, 44f);
            this.titleRect = new Rect(nodeRect.x, (float)((double)nodeRect.yMax - (!BehaviorDesignerPreferences.GetBool(BDPreferences.CompactMode) ? 20.0 : 28.0) - 1.0), nodeRect.width, 20f);
            this.breakpointTextureRect = new Rect(nodeRect.xMax - 16f, nodeRect.y + 3f, 14f, 14f);
            this.errorTextureRect = new Rect(nodeRect.xMax - 12f, nodeRect.y - 8f, 20f, 20f);
            this.referenceTextureRect = new Rect(nodeRect.x + 2f, nodeRect.y + 3f, 14f, 14f);
            this.conditionalAbortTextureRect = new Rect(nodeRect.x + 3f, nodeRect.y + 3f, 16f, 16f);
            this.conditionalAbortLowerPriorityTextureRect = new Rect(nodeRect.x + 3f, nodeRect.y, 16f, 16f);
            this.disabledButtonTextureRect = new Rect(nodeRect.x - 1f, nodeRect.y - 17f, 14f, 14f);
            this.collapseButtonTextureRect = new Rect(nodeRect.x + 15f, nodeRect.y - 17f, 14f, 14f);
            this.incomingConnectionTextureRect = new Rect(nodeRect.x + (float)(((double)nodeRect.width - 42.0) / 2.0), (float)((double)nodeRect.y - 14.0 - 3.0 + 3.0), 42f, 17f);
            this.outgoingConnectionTextureRect = new Rect(nodeRect.x + (float)(((double)nodeRect.width - 42.0) / 2.0), nodeRect.yMax - 3f, 42f, 19f);
            this.successReevaluatingExecutionStatusTextureRect = new Rect(nodeRect.xMax - 37f, nodeRect.yMax - 38f, 35f, 36f);
            this.successExecutionStatusTextureRect = new Rect(nodeRect.xMax - 37f, nodeRect.yMax - 33f, 35f, 31f);
            this.failureExecutionStatusTextureRect = new Rect(nodeRect.xMax - 37f, nodeRect.yMax - 38f, 35f, 36f);
            this.iconBorderTextureRect = new Rect(nodeRect.x + (float)(((double)nodeRect.width - 46.0) / 2.0), (float)((double)nodeRect.y + 3.0 + 2.0), 46f, 46f);
            this.CalculateNodeCommentRect(nodeRect);
            this.mCacheIsDirty = false;
        }

        private void CalculateNodeCommentRect(Rect nodeRect)
        {
            bool flag = false;
            if (this.mTask.NodeData.WatchedFields != null && this.mTask.NodeData.WatchedFields.Count > 0)
            {
                string str1 = string.Empty;
                string str2 = string.Empty;
                for (int index = 0; index < this.mTask.NodeData.WatchedFields.Count; ++index)
                {
                    FieldInfo watchedField = this.mTask.NodeData.WatchedFields[index];
                    str1 = str1 + BehaviorDesignerUtility.SplitCamelCase(watchedField.Name) + ": \n";
                    str2 = str2 + (watchedField.GetValue((object)this.mTask) == null ? "null" : watchedField.GetValue((object)this.mTask).ToString()) + "\n";
                }

                float num1;
                float num2;
                BehaviorDesignerUtility.TaskCommentGUIStyle.CalcMinMaxWidth(new GUIContent(str1), out num1, out num2);
                float num3;
                BehaviorDesignerUtility.TaskCommentGUIStyle.CalcMinMaxWidth(new GUIContent(str2), out num1, out num3);
                float width1 = num2;
                float width2 = num3;
                float num4 = Mathf.Min(220f, (float)((double)num2 + (double)num3 + 20.0));
                if ((double)num4 == 220.0)
                {
                    width1 = (float)((double)num2 / ((double)num2 + (double)num3) * 220.0);
                    width2 = (float)((double)num3 / ((double)num2 + (double)num3) * 220.0);
                }

                this.watchedFieldRect = new Rect(nodeRect.xMax + 4f, nodeRect.y, num4 + 8f, nodeRect.height);
                this.watchedFieldNamesRect = new Rect(nodeRect.xMax + 6f, nodeRect.y + 4f, width1, nodeRect.height - 8f);
                this.watchedFieldValuesRect = new Rect(nodeRect.xMax + 6f + width1, nodeRect.y + 4f, width2, nodeRect.height - 8f);
                flag = true;
            }

            string nodeComment = this.GetNodeComment();
            if (nodeComment.Equals(string.Empty))
                return;
            if (this.isParent)
            {
                float num5;
                float num6;
                BehaviorDesignerUtility.TaskCommentGUIStyle.CalcMinMaxWidth(new GUIContent(nodeComment), out num5, out num6);
                float width = Mathf.Min(220f, num6 + 20f);
                if (flag)
                {
                    this.commentRect = new Rect(nodeRect.xMin - 12f - width, nodeRect.y, width + 8f, nodeRect.height);
                    this.commentLabelRect = new Rect(nodeRect.xMin - 6f - width, nodeRect.y + 4f, width, nodeRect.height - 8f);
                }
                else
                {
                    this.commentRect = new Rect(nodeRect.xMax + 4f, nodeRect.y, width + 8f, nodeRect.height);
                    this.commentLabelRect = new Rect(nodeRect.xMax + 6f, nodeRect.y + 4f, width, nodeRect.height - 8f);
                }
            }
            else
            {
                float height = Mathf.Min(100f, BehaviorDesignerUtility.TaskCommentGUIStyle.CalcHeight(new GUIContent(nodeComment), nodeRect.width - 4f));
                this.commentRect = new Rect(nodeRect.x, nodeRect.yMax + 4f, nodeRect.width, height + 4f);
                this.commentLabelRect = new Rect(nodeRect.x, nodeRect.yMax + 4f, nodeRect.width - 4f, height);
            }
        }

        private string GetNodeComment()
        {
            string nodeComment = string.Empty;
            if (!this.mTask.OnDrawNodeText().Equals(string.Empty))
                nodeComment = this.mTask.OnDrawNodeText();
            if (!this.mTask.NodeData.Comment.Equals(string.Empty))
            {
                if (!nodeComment.Equals(string.Empty))
                    nodeComment += "\n";
                nodeComment += this.mTask.NodeData.Comment;
            }

            return nodeComment;
        }

        public bool DrawNode(Vector2 offset, bool drawSelected, bool disabled)
        {
            if (drawSelected != this.mSelected)
                return false;
            if (((object)this).ToString().Length != this.prevFriendlyNameLength)
            {
                this.prevFriendlyNameLength = ((object)this).ToString().Length;
                this.mRectIsDirty = true;
            }

            Rect nodeRect = this.Rectangle(offset, false, false);
            this.UpdateCache(nodeRect);
            bool flag1 = (double)this.mTask.NodeData.PushTime != -1.0 && (double)this.mTask.NodeData.PushTime >= (double)this.mTask.NodeData.PopTime ||
                         this.isEntryDisplay && this.outgoingNodeConnections.Count > 0 && (double)this.outgoingNodeConnections[0].DestinationNodeDesigner.Task.NodeData.PushTime != -1.0;
            bool flag2 = this.mIdentifyUpdateCount != -1 || this.mFoundTask;
            bool flag3 = this.prevRunningState != flag1;
            float num1 = !BehaviorDesignerPreferences.GetBool(BDPreferences.FadeNodes) ? 0.01f : 0.5f;
            float num2 = 0.0f;
            if (flag2)
            {
                num2 = 2000 - this.mIdentifyUpdateCount >= 500 ? 1f : (float)(2000 - this.mIdentifyUpdateCount) / 500f;
                if (this.mIdentifyUpdateCount != -1)
                {
                    ++this.mIdentifyUpdateCount;
                    if (this.mIdentifyUpdateCount > 2000)
                        this.mIdentifyUpdateCount = -1;
                }

                flag3 = true;
            }
            else if (flag1)
                num2 = 1f;
            else if ((double)this.mTask.NodeData.PopTime != -1.0 && (double)num1 != 0.0 && (double)this.mTask.NodeData.PopTime <= (double)Time.realtimeSinceStartup && (double)Time.realtimeSinceStartup - (double)this.mTask.NodeData.PopTime < (double)num1 || this.isEntryDisplay &&
                     this.outgoingNodeConnections.Count > 0 && (double)this.outgoingNodeConnections[0].DestinationNodeDesigner.Task.NodeData.PopTime != -1.0 && (double)this.outgoingNodeConnections[0].DestinationNodeDesigner.Task.NodeData.PopTime <= (double)Time.realtimeSinceStartup &&
                     (double)Time.realtimeSinceStartup - (double)this.outgoingNodeConnections[0].DestinationNodeDesigner.Task.NodeData.PopTime < (double)num1)
            {
                num2 = !this.isEntryDisplay
                    ? (float)(1.0 - ((double)Time.realtimeSinceStartup - (double)this.mTask.NodeData.PopTime) / (double)num1)
                    : (float)(1.0 - ((double)Time.realtimeSinceStartup - (double)this.outgoingNodeConnections[0].DestinationNodeDesigner.Task.NodeData.PopTime) / (double)num1);
                flag3 = true;
            }

            if (!this.isEntryDisplay && !this.prevRunningState && (UnityEngine.Object)this.parentNodeDesigner != (UnityEngine.Object)null)
                this.parentNodeDesigner.BringConnectionToFront(this);
            this.prevRunningState = flag1;
            if ((double)num2 != 1.0)
            {
                GUI.color = disabled || this.mTask.Disabled ? this.grayColor : Color.white;
                GUIStyle backgroundGUIStyle = !BehaviorDesignerPreferences.GetBool(BDPreferences.CompactMode)
                    ? (!this.mSelected ? BehaviorDesignerUtility.GetTaskGUIStyle(this.mTask.NodeData.ColorIndex) : BehaviorDesignerUtility.GetTaskSelectedGUIStyle(this.mTask.NodeData.ColorIndex))
                    : (!this.mSelected ? BehaviorDesignerUtility.GetTaskCompactGUIStyle(this.mTask.NodeData.ColorIndex) : BehaviorDesignerUtility.GetTaskSelectedCompactGUIStyle(this.mTask.NodeData.ColorIndex));
                this.DrawNodeTexture(nodeRect,
                    BehaviorDesignerUtility.GetTaskConnectionTopTexture(this.mTask.NodeData.ColorIndex),
                    BehaviorDesignerUtility.GetTaskConnectionBottomTexture(this.mTask.NodeData.ColorIndex),
                    backgroundGUIStyle,
                    BehaviorDesignerUtility.GetTaskBorderTexture(this.mTask.NodeData.ColorIndex));
            }

            if ((double)num2 > 0.0)
            {
                GUIStyle backgroundGUIStyle;
                Texture2D iconBorderTexture;
                if (flag2)
                {
                    backgroundGUIStyle = !BehaviorDesignerPreferences.GetBool(BDPreferences.CompactMode)
                        ? (!this.mSelected ? BehaviorDesignerUtility.TaskIdentifyGUIStyle : BehaviorDesignerUtility.TaskIdentifySelectedGUIStyle)
                        : (!this.mSelected ? BehaviorDesignerUtility.TaskIdentifyCompactGUIStyle : BehaviorDesignerUtility.TaskIdentifySelectedCompactGUIStyle);
                    iconBorderTexture = BehaviorDesignerUtility.TaskBorderIdentifyTexture;
                }
                else
                {
                    backgroundGUIStyle = !BehaviorDesignerPreferences.GetBool(BDPreferences.CompactMode)
                        ? (!this.mSelected ? BehaviorDesignerUtility.TaskRunningGUIStyle : BehaviorDesignerUtility.TaskRunningSelectedGUIStyle)
                        : (!this.mSelected ? BehaviorDesignerUtility.TaskRunningCompactGUIStyle : BehaviorDesignerUtility.TaskRunningSelectedCompactGUIStyle);
                    iconBorderTexture = BehaviorDesignerUtility.TaskBorderRunningTexture;
                }

                GUI.color = (disabled || this.mTask.Disabled ? this.grayColor : new Color(1, 1, 1, num2));
                Texture2D connectionTopTexture = (Texture2D)null;
                Texture2D connectionBottomTexture = (Texture2D)null;
                if (!this.isEntryDisplay)
                    connectionTopTexture = !flag2 ? BehaviorDesignerUtility.TaskConnectionRunningTopTexture : BehaviorDesignerUtility.TaskConnectionIdentifyTopTexture;
                if (this.isParent)
                    connectionBottomTexture = !flag2 ? BehaviorDesignerUtility.TaskConnectionRunningBottomTexture : BehaviorDesignerUtility.TaskConnectionIdentifyBottomTexture;
                this.DrawNodeTexture(nodeRect, connectionTopTexture, connectionBottomTexture, backgroundGUIStyle, iconBorderTexture);
                GUI.color = Color.white;
            }

            if (this.mTask.NodeData.Collapsed)
                GUI.DrawTexture(this.nodeCollapsedTextureRect, (Texture)BehaviorDesignerUtility.TaskConnectionCollapsedTexture);
            if (!BehaviorDesignerPreferences.GetBool(BDPreferences.CompactMode))
                GUI.DrawTexture(this.iconTextureRect, this.mTask.NodeData.Icon);
            if ((double)this.mTask.NodeData.InterruptTime != -1.0 && (double)Time.realtimeSinceStartup - (double)this.mTask.NodeData.InterruptTime < 0.75 + (double)num1)
            {
                float num3 = (double)Time.realtimeSinceStartup - (double)this.mTask.NodeData.InterruptTime >= 0.75 ? (float)(1.0 - ((double)Time.realtimeSinceStartup - ((double)this.mTask.NodeData.InterruptTime + 0.75)) / (double)num1) : 1f;
                GUI.color = new Color(1, 1, 1, num3);
                GUI.Label(nodeRect, string.Empty, BehaviorDesignerUtility.TaskHighlightGUIStyle);
                GUI.color = Color.white;
            }

            GUI.Label(this.titleRect, ((object)this).ToString(), BehaviorDesignerUtility.TaskTitleGUIStyle);
            if (this.mTask.NodeData.IsBreakpoint)
                GUI.DrawTexture(this.breakpointTextureRect, (Texture)BehaviorDesignerUtility.BreakpointTexture);
            if (this.showReferenceIcon)
                GUI.DrawTexture(this.referenceTextureRect, (Texture)BehaviorDesignerUtility.ReferencedTexture);
            if (this.hasError)
                GUI.DrawTexture(this.errorTextureRect, (Texture)BehaviorDesignerUtility.ErrorIconTexture);
            if (this.mTask is Composite && (this.mTask as Composite).AbortType != null)
            {
                AbortType abortType = (this.mTask as Composite).AbortType;
                if (abortType != AbortType.Self)
                {
                    if (abortType != AbortType.LowerPriority)
                    {
                        if (abortType == AbortType.Both)
                            GUI.DrawTexture(this.conditionalAbortTextureRect, (Texture)BehaviorDesignerUtility.ConditionalAbortBothTexture);
                    }
                    else
                        GUI.DrawTexture(this.conditionalAbortLowerPriorityTextureRect, (Texture)BehaviorDesignerUtility.ConditionalAbortLowerPriorityTexture);
                }
                else
                    GUI.DrawTexture(this.conditionalAbortTextureRect, (Texture)BehaviorDesignerUtility.ConditionalAbortSelfTexture);
            }

            GUI.color = Color.white;
            if (this.showHoverBar)
            {
                GUI.DrawTexture(this.disabledButtonTextureRect, !this.mTask.Disabled ? (Texture)BehaviorDesignerUtility.DisableTaskTexture : (Texture)BehaviorDesignerUtility.EnableTaskTexture, (ScaleMode)2);
                if (this.isParent || this.mTask is BehaviorReference)
                {
                    bool collapsed = this.mTask.NodeData.Collapsed;
                    if (this.mTask is BehaviorReference)
                        collapsed = (this.mTask as BehaviorReference).collapsed;
                    GUI.DrawTexture(this.collapseButtonTextureRect, !collapsed ? (Texture)BehaviorDesignerUtility.CollapseTaskTexture : (Texture)BehaviorDesignerUtility.ExpandTaskTexture, (ScaleMode)2);
                }
            }

            return flag3;
        }

        private void DrawNodeTexture(
            Rect nodeRect,
            Texture2D connectionTopTexture,
            Texture2D connectionBottomTexture,
            GUIStyle backgroundGUIStyle,
            Texture2D iconBorderTexture)
        {
            if (!this.isEntryDisplay)
                GUI.DrawTexture(this.incomingConnectionTextureRect, (Texture)connectionTopTexture, (ScaleMode)2);
            if (this.isParent)
                GUI.DrawTexture(this.outgoingConnectionTextureRect, (Texture)connectionBottomTexture, (ScaleMode)2);
            GUI.Label(nodeRect, string.Empty, backgroundGUIStyle);
            if (this.mTask.NodeData.ExecutionStatus == TaskStatus.Success)
            {
                if (this.mTask.NodeData.IsReevaluating)
                    GUI.DrawTexture(this.successReevaluatingExecutionStatusTextureRect, (Texture)BehaviorDesignerUtility.ExecutionSuccessRepeatTexture);
                else
                    GUI.DrawTexture(this.successExecutionStatusTextureRect, (Texture)BehaviorDesignerUtility.ExecutionSuccessTexture);
            }
            else if (this.mTask.NodeData.ExecutionStatus == TaskStatus.Failure)
                GUI.DrawTexture(this.failureExecutionStatusTextureRect, !this.mTask.NodeData.IsReevaluating ? (Texture)BehaviorDesignerUtility.ExecutionFailureTexture : (Texture)BehaviorDesignerUtility.ExecutionFailureRepeatTexture);

            if (BehaviorDesignerPreferences.GetBool(BDPreferences.CompactMode))
                return;
            GUI.DrawTexture(this.iconBorderTextureRect, (Texture)iconBorderTexture);
        }

        public void DrawNodeConnection(Vector2 offset, bool disabled)
        {
            if (this.mConnectionIsDirty)
            {
                this.DetermineConnectionHorizontalHeight(this.Rectangle(offset, false, false), offset);
                this.mConnectionIsDirty = false;
            }

            if (!this.isParent)
                return;
            for (int index = 0; index < this.outgoingNodeConnections.Count; ++index)
                this.outgoingNodeConnections[index].DrawConnection(offset, disabled);
        }

        public void DrawNodeComment(Vector2 offset)
        {
            string nodeComment = this.GetNodeComment();
            if (nodeComment.Length != this.prevCommentLength)
            {
                this.prevCommentLength = nodeComment.Length;
                this.mRectIsDirty = true;
            }

            if (this.mTask.NodeData.WatchedFields != null && this.mTask.NodeData.WatchedFields.Count > 0)
            {
                if (this.mTask.NodeData.WatchedFields.Count != this.prevWatchedFieldsLength.Count)
                {
                    this.mRectIsDirty = true;
                    this.prevWatchedFieldsLength.Clear();
                    for (int index = 0; index < this.mTask.NodeData.WatchedFields.Count; ++index)
                    {
                        if (!(this.mTask.NodeData.WatchedFields[index] == (FieldInfo)null))
                        {
                            object obj = this.mTask.NodeData.WatchedFields[index].GetValue((object)this.mTask);
                            if (obj != null)
                                this.prevWatchedFieldsLength.Add(obj.ToString().Length);
                            else
                                this.prevWatchedFieldsLength.Add(0);
                        }
                    }
                }
                else
                {
                    for (int index = 0; index < this.mTask.NodeData.WatchedFields.Count; ++index)
                    {
                        if (!(this.mTask.NodeData.WatchedFields[index] == (FieldInfo)null))
                        {
                            object obj = this.mTask.NodeData.WatchedFields[index].GetValue((object)this.mTask);
                            int num = 0;
                            if (obj != null)
                                num = obj.ToString().Length;
                            if (num != this.prevWatchedFieldsLength[index])
                            {
                                this.mRectIsDirty = true;
                                break;
                            }
                        }
                    }
                }
            }

            if (nodeComment.Equals(string.Empty) && (this.mTask.NodeData.WatchedFields == null || this.mTask.NodeData.WatchedFields.Count == 0))
                return;
            if (this.mTask.NodeData.WatchedFields != null && this.mTask.NodeData.WatchedFields.Count > 0)
            {
                string str1 = string.Empty;
                string str2 = string.Empty;
                for (int index = 0; index < this.mTask.NodeData.WatchedFields.Count; ++index)
                {
                    FieldInfo watchedField = this.mTask.NodeData.WatchedFields[index];
                    str1 = str1 + BehaviorDesignerUtility.SplitCamelCase(watchedField.Name) + ": \n";
                    str2 = str2 + (watchedField.GetValue((object)this.mTask) == null ? "null" : watchedField.GetValue((object)this.mTask).ToString()) + "\n";
                }

                GUI.Box(this.watchedFieldRect, string.Empty, BehaviorDesignerUtility.TaskDescriptionGUIStyle);
                GUI.Label(this.watchedFieldNamesRect, str1, BehaviorDesignerUtility.TaskCommentRightAlignGUIStyle);
                GUI.Label(this.watchedFieldValuesRect, str2, BehaviorDesignerUtility.TaskCommentLeftAlignGUIStyle);
            }

            if (nodeComment.Equals(string.Empty))
                return;
            GUI.Box(this.commentRect, string.Empty, BehaviorDesignerUtility.TaskDescriptionGUIStyle);
            GUI.Label(this.commentLabelRect, nodeComment, BehaviorDesignerUtility.TaskCommentGUIStyle);
        }

        public bool Contains(Vector2 point, Vector2 offset, bool includeConnections) => this.Rectangle(offset, includeConnections, false).Contains(point);

        public NodeConnection NodeConnectionRectContains(Vector2 point, Vector2 offset)
        {
            Rect rect = this.IncomingConnectionRect(offset);
            bool incomingNodeConnection;
            return (incomingNodeConnection = rect.Contains(point)) || this.isParent && this.OutgoingConnectionRect(offset).Contains(point) ? this.CreateNodeConnection(incomingNodeConnection) : (NodeConnection)null;
        }

        public NodeConnection CreateNodeConnection(bool incomingNodeConnection)
        {
            NodeConnection instance = ScriptableObject.CreateInstance<NodeConnection>();
            instance.LoadConnection(this, !incomingNodeConnection ? NodeConnectionType.Outgoing : NodeConnectionType.Incoming);
            return instance;
        }

        public void ConnectionContains(
            Vector2 point,
            Vector2 offset,
            ref List<NodeConnection> nodeConnections)
        {
            if (this.outgoingNodeConnections == null || this.isEntryDisplay)
                return;
            for (int index = 0; index < this.outgoingNodeConnections.Count; ++index)
            {
                if (this.outgoingNodeConnections[index].Contains(point, offset))
                    nodeConnections.Add(this.outgoingNodeConnections[index]);
            }
        }

        private void DetermineConnectionHorizontalHeight(Rect nodeRect, Vector2 offset)
        {
            if (!this.isParent)
                return;
            float num1 = float.MaxValue;
            float num2 = num1;
            for (int index = 0; index < this.outgoingNodeConnections.Count; ++index)
            {
                Rect rect = this.outgoingNodeConnections[index].DestinationNodeDesigner.Rectangle(offset, false, false);
                if ((double)rect.y < (double)num1)
                {
                    num1 = rect.y;
                    num2 = rect.y;
                }
            }

            float num3 = (float)((double)num1 * 0.75 + (double)nodeRect.yMax * 0.25);
            if ((double)num3 < (double)nodeRect.yMax + 15.0)
                num3 = nodeRect.yMax + 15f;
            else if ((double)num3 > (double)num2 - 15.0)
                num3 = num2 - 15f;
            for (int index = 0; index < this.outgoingNodeConnections.Count; ++index)
                this.outgoingNodeConnections[index].HorizontalHeight = num3;
        }

        public Vector2 GetConnectionPosition(Vector2 offset, NodeConnectionType connectionType)
        {
            Vector2 connectionPosition;
            if (connectionType == NodeConnectionType.Incoming)
            {
                Rect rect = this.IncomingConnectionRect(offset);
                connectionPosition = new Vector2(rect.center.x, rect.y + 7f);
            }
            else
            {
                Rect rect = this.OutgoingConnectionRect(offset);
                connectionPosition = new Vector2(rect.center.x, rect.yMax - 8f);
            }

            return connectionPosition;
        }

        public bool HoverBarAreaContains(Vector2 point, Vector2 offset)
        {
            Rect rect = this.Rectangle(offset, false, false);
            rect.y -= 24f;
            return rect.Contains(point);
        }

        public bool HoverBarButtonClick(Vector2 point, Vector2 offset, ref bool collapsedButtonClicked)
        {
            Rect rect1 = this.Rectangle(offset, false, false);
            Rect rect2 = new Rect(rect1.x - 1f, rect1.y - 17f, 14f, 14f);
            Rect rect3 = rect2;
            bool flag = false;
            if (rect2.Contains(point))
            {
                this.mTask.Disabled = !this.mTask.Disabled;
                flag = true;
            }

            if (!flag && (this.isParent || this.mTask is BehaviorReference))
            {
                Rect rect4 = new Rect(rect1.x + 15f, rect1.y - 17f, 14f, 14f);
                rect3.xMax = rect4.xMax;
                if (rect4.Contains(point))
                {
                    if (this.mTask is BehaviorReference)
                        (this.mTask as BehaviorReference).collapsed = !(this.mTask as BehaviorReference).collapsed;
                    else
                        this.mTask.NodeData.Collapsed = !this.mTask.NodeData.Collapsed;
                    collapsedButtonClicked = true;
                    flag = true;
                }
            }

            if (!flag && rect3.Contains(point))
                flag = true;
            return flag;
        }

        public bool Intersects(Rect rect, Vector2 offset)
        {
            Rect rect1 = this.Rectangle(offset, false, false);
            return (double)rect1.xMin < (double)rect.xMax && (double)rect1.xMax > (double)rect.xMin && (double)rect1.yMin < (double)rect.yMax && (double)rect1.yMax > (double)rect.yMin;
        }

        public void ChangeOffset(Vector2 delta)
        {
            Vector2 vector = (Vector2)this.mTask.NodeData.Offset + delta;
            this.mTask.NodeData.Offset = vector;
            this.MarkDirty();
            if (this.parentNodeDesigner != null)
            {
                this.parentNodeDesigner.MarkDirty();
            }
        }

        public void AddChildNode(
            NodeDesigner childNodeDesigner,
            NodeConnection nodeConnection,
            bool adjustOffset,
            bool replaceNode)
        {
            this.AddChildNode(childNodeDesigner, nodeConnection, adjustOffset, replaceNode, -1);
        }

        public void AddChildNode(
            NodeDesigner childNodeDesigner,
            NodeConnection nodeConnection,
            bool adjustOffset,
            bool replaceNode,
            int replaceNodeIndex)
        {
            if (replaceNode)
            {
                (this.mTask as ParentTask).Children[replaceNodeIndex] = childNodeDesigner.Task;
            }
            else
            {
                if (!this.isEntryDisplay)
                {
                    ParentTask mTask = this.mTask as ParentTask;
                    int num = 0;
                    if (mTask.Children != null)
                    {
                        num = 0;
                        while (num < mTask.Children.Count)
                        {
                            Vector2 absolutePosition = childNodeDesigner.GetAbsolutePosition();
                            Vector2 vector2 = (mTask.Children[num].NodeData.NodeDesigner as NodeDesigner).GetAbsolutePosition();
                            if (absolutePosition.x < vector2.x)
                            {
                                break;
                            }

                            num++;
                        }
                    }

                    mTask.AddChild(childNodeDesigner.Task, num);
                }

                if (adjustOffset)
                {
                    NodeData data1 = childNodeDesigner.Task.NodeData;
                    data1.Offset = (Vector2)data1.Offset - this.GetAbsolutePosition();
                }
            }

            childNodeDesigner.ParentNodeDesigner = this;
            nodeConnection.DestinationNodeDesigner = childNodeDesigner;
            nodeConnection.NodeConnectionType = NodeConnectionType.Fixed;
            if (!nodeConnection.OriginatingNodeDesigner.Equals(this))
            {
                nodeConnection.OriginatingNodeDesigner = this;
            }

            this.outgoingNodeConnections.Add(nodeConnection);
            this.mConnectionIsDirty = true;
        }

        public void RemoveChildNode(NodeDesigner childNodeDesigner)
        {
            if (!this.isEntryDisplay)
                (this.mTask as ParentTask).Children.Remove(childNodeDesigner.Task);
            for (int index = 0; index < this.outgoingNodeConnections.Count; ++index)
            {
                NodeConnection outgoingNodeConnection = this.outgoingNodeConnections[index];
                if (((object)outgoingNodeConnection.DestinationNodeDesigner).Equals((object)childNodeDesigner) || ((object)outgoingNodeConnection.OriginatingNodeDesigner).Equals((object)childNodeDesigner))
                {
                    this.outgoingNodeConnections.RemoveAt(index);
                    break;
                }
            }

            childNodeDesigner.ParentNodeDesigner = (NodeDesigner)null;
            this.mConnectionIsDirty = true;
        }

        public void SetID(ref int id)
        {
            Task mTask1 = this.mTask;
            int num1;
            id = (num1 = id) + 1;
            int num2 = num1;
            mTask1.ID = num2;
            if (!this.isParent)
                return;
            ParentTask mTask2 = this.mTask as ParentTask;
            if (mTask2.Children == null)
                return;
            for (int index = 0; index < mTask2.Children.Count; ++index)
                (mTask2.Children[index].NodeData.NodeDesigner as NodeDesigner).SetID(ref id);
        }

        public int ChildIndexForTask(Task childTask)
        {
            if (this.isParent)
            {
                ParentTask mTask = this.mTask as ParentTask;
                if (mTask.Children != null)
                {
                    for (int index = 0; index < mTask.Children.Count; ++index)
                    {
                        if (((object)mTask.Children[index]).Equals((object)childTask))
                            return index;
                    }
                }
            }

            return -1;
        }

        public NodeDesigner NodeDesignerForChildIndex(int index)
        {
            if (index < 0)
                return (NodeDesigner)null;
            if (this.isParent)
            {
                ParentTask mTask = this.mTask as ParentTask;
                if (mTask.Children != null)
                    return index >= mTask.Children.Count || mTask.Children[index] == null ? (NodeDesigner)null : mTask.Children[index].NodeData.NodeDesigner as NodeDesigner;
            }

            return (NodeDesigner)null;
        }

        public void MoveChildNode(int index, bool decreaseIndex)
        {
            int index1 = index + (!decreaseIndex ? 1 : -1);
            ParentTask mTask = this.mTask as ParentTask;
            Task child = mTask.Children[index];
            mTask.Children[index] = mTask.Children[index1];
            mTask.Children[index1] = child;
        }

        private void BringConnectionToFront(NodeDesigner nodeDesigner)
        {
            for (int index = 0; index < this.outgoingNodeConnections.Count; ++index)
            {
                if (((object)this.outgoingNodeConnections[index].DestinationNodeDesigner).Equals((object)nodeDesigner))
                {
                    NodeConnection outgoingNodeConnection = this.outgoingNodeConnections[index];
                    this.outgoingNodeConnections[index] = this.outgoingNodeConnections[this.outgoingNodeConnections.Count - 1];
                    this.outgoingNodeConnections[this.outgoingNodeConnections.Count - 1] = outgoingNodeConnection;
                    break;
                }
            }
        }

        public void ToggleBreakpoint() => this.mTask.NodeData.IsBreakpoint = !this.Task.NodeData.IsBreakpoint;

        public void ToggleEnableState() => this.mTask.Disabled = !this.Task.Disabled;

        public bool IsDisabled()
        {
            if (this.mTask.Disabled)
                return true;
            return (UnityEngine.Object)this.parentNodeDesigner != (UnityEngine.Object)null && this.parentNodeDesigner.IsDisabled();
        }

        public bool ToggleCollapseState()
        {
            this.mTask.NodeData.Collapsed = !this.Task.NodeData.Collapsed;
            return this.mTask.NodeData.Collapsed;
        }

        public void IdentifyNode() => this.mIdentifyUpdateCount = 0;

        public void FoundTask(bool found) => this.mFoundTask = found;

        public bool HasParent(NodeDesigner nodeDesigner)
        {
            if ((UnityEngine.Object)this.parentNodeDesigner == (UnityEngine.Object)null)
                return false;
            return ((object)this.parentNodeDesigner).Equals((object)nodeDesigner) || this.parentNodeDesigner.HasParent(nodeDesigner);
        }

        public void DestroyConnections()
        {
            if (this.outgoingNodeConnections == null)
                return;
            for (int index = this.outgoingNodeConnections.Count - 1; index > -1; --index)
                UnityEngine.Object.DestroyImmediate((UnityEngine.Object)this.outgoingNodeConnections[index], true);
        }

        public override bool Equals(object obj) => object.ReferenceEquals((object)this, obj);

        public override int GetHashCode() => base.GetHashCode();

        public override string ToString()
        {
            if (this.mTask == null)
                return string.Empty;
            return this.mTask.FriendlyName.Equals(string.Empty) ? this.taskName : this.mTask.FriendlyName;
        }
    }
}