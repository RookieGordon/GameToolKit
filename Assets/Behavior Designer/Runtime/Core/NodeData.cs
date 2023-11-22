// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.NodeData
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections.Generic;
using System.Reflection;
using BehaviorDesigner.Runtime.Tasks;
using Unity.Mathematics;
using SerializeField = Newtonsoft.Json.JsonPropertyAttribute;

namespace BehaviorDesigner.Runtime
{
    [Serializable]
    public partial class NodeData
    {
      
#if !UNITY_EDITOR
        [SerializeField]
        private object nodeDesigner;

        [SerializeField]
        private float2 offset;

        [SerializeField]
        private string friendlyName = string.Empty;

        [SerializeField]
        private string comment = string.Empty;

        [SerializeField]
        private bool isBreakpoint;

        [SerializeField]
        private bool collapsed;

        [SerializeField]
        private int colorIndex;

        [SerializeField]
        private List<string> watchedFieldNames;
#endif

        private List<FieldInfo> watchedFields;

        private float pushTime = -1f;

        private float popTime = -1f;

        private float interruptTime = -1f;

        private bool isReevaluating;

        private TaskStatus executionStatus;

        public object NodeDesigner
        {
            get => this.nodeDesigner;
            set => this.nodeDesigner = value;
        }

        public float2 Offset
        {
            get => this.offset;
            set => this.offset = value;
        }

        public string FriendlyName
        {
            get => this.friendlyName;
            set => this.friendlyName = value;
        }

        public string Comment
        {
            get => this.comment;
            set => this.comment = value;
        }

        public bool IsBreakpoint
        {
            get => this.isBreakpoint;
            set => this.isBreakpoint = value;
        }

        public bool Collapsed
        {
            get => this.collapsed;
            set => this.collapsed = value;
        }

        public int ColorIndex
        {
            get => this.colorIndex;
            set => this.colorIndex = value;
        }

        public List<string> WatchedFieldNames
        {
            get => this.watchedFieldNames;
            set => this.watchedFieldNames = value;
        }

        public List<FieldInfo> WatchedFields
        {
            get => this.watchedFields;
            set => this.watchedFields = value;
        }

        public float PushTime
        {
            get => this.pushTime;
            set => this.pushTime = value;
        }

        public float PopTime
        {
            get => this.popTime;
            set => this.popTime = value;
        }

        public float InterruptTime
        {
            get => this.interruptTime;
            set => this.interruptTime = value;
        }

        public bool IsReevaluating
        {
            get => this.isReevaluating;
            set => this.isReevaluating = value;
        }

        public TaskStatus ExecutionStatus
        {
            get => this.executionStatus;
            set => this.executionStatus = value;
        }

        public void InitWatchedFields(Task task)
        {
            if (this.watchedFieldNames == null || this.watchedFieldNames.Count <= 0)
            {
                return;
            }
            this.watchedFields = new List<FieldInfo>();
            for (int index = 0; index < this.watchedFieldNames.Count; ++index)
            {
                FieldInfo field = task.GetType()
                    .GetField(
                        this.watchedFieldNames[index],
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    );
                if (field != (FieldInfo)null)
                {
                    this.watchedFields.Add(field);
                }
            }
        }

        public void CopyFrom(NodeData nodeData, Task task)
        {
            this.nodeDesigner = nodeData.NodeDesigner;
            this.offset = nodeData.Offset;
            this.comment = nodeData.Comment;
            this.isBreakpoint = nodeData.IsBreakpoint;
            this.collapsed = nodeData.Collapsed;
            if (nodeData.WatchedFields == null || nodeData.WatchedFields.Count <= 0)
            {
                return;
            }
            this.watchedFields = new List<FieldInfo>();
            this.watchedFieldNames = new List<string>();
            for (int index = 0; index < nodeData.watchedFields.Count; ++index)
            {
                FieldInfo field = task.GetType()
                    .GetField(
                        nodeData.WatchedFields[index].Name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    );
                if (field != (FieldInfo)null)
                {
                    this.watchedFields.Add(field);
                    this.watchedFieldNames.Add(field.Name);
                }
            }
        }

        public int GetWatchedFieldIndex(FieldInfo field)
        {
            if (this.watchedFields == null)
            {
                return -1;
            }
            for (int index = 0; index < this.watchedFields.Count; ++index)
            {
                if (
                    !(this.watchedFields[index] == (FieldInfo)null)
                    && this.watchedFields[index].FieldType == field.FieldType
                    && this.watchedFields[index].Name == field.Name
                )
                {
                    return index;
                }
            }
            return -1;
        }

        public void AddWatchedField(FieldInfo field)
        {
            if (this.watchedFields == null)
            {
                this.watchedFields = new List<FieldInfo>();
                this.watchedFieldNames = new List<string>();
            }
            if (this.GetWatchedFieldIndex(field) != -1)
            {
                return;
            }
            this.watchedFields.Add(field);
            this.watchedFieldNames.Add(field.Name);
        }

        public void RemoveWatchedField(FieldInfo field)
        {
            int watchedFieldIndex = this.GetWatchedFieldIndex(field);
            if (watchedFieldIndex == -1)
            {
                return;
            }

            this.watchedFields.RemoveAt(watchedFieldIndex);
            this.watchedFieldNames.RemoveAt(watchedFieldIndex);
        }

        private static float2 StringToVector2(string vector2String)
        {
            string[] strArray = vector2String.Substring(1, vector2String.Length - 2).Split(',');
            return new float2(float.Parse(strArray[0]), float.Parse(strArray[1]));
        }
    }
}
