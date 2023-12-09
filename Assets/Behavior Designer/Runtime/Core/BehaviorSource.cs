// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.BehaviorSource
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections.Generic;
using BehaviorDesigner.Runtime.Tasks;
using Newtonsoft.Json;
using Debug = BehaviorDesigner.Runtime.BehaviorDebug;

namespace BehaviorDesigner.Runtime
{
    public partial class BehaviorSource : IVariableSource
    {
        public string behaviorName = "Behavior";

        public string behaviorDescription = string.Empty;

        private int behaviorID = -1;

        private Task mEntryTask;

        private Task mRootTask;

        private List<Task> mDetachedTasks;

        private List<SharedVariable> mVariables;

        private Dictionary<string, int> mSharedVariableIndex;

#if !UNITY_PLATFORM
        private bool mHasSerialized;

        [JsonProperty]
        private TaskSerializationData mTaskData;

        [JsonProperty]
        private IBehavior mOwner;
#endif

        public BehaviorSource()
        {
        }

        public BehaviorSource(IBehavior owner)
        {
            this.Initialize(owner);
        }

        public int BehaviorID
        {
            get => this.behaviorID;
            set => this.behaviorID = value;
        }

        public Task EntryTask
        {
            get => this.mEntryTask;
            set => this.mEntryTask = value;
        }

        public Task RootTask
        {
            get => this.mRootTask;
            set => this.mRootTask = value;
        }

        public List<Task> DetachedTasks
        {
            get => this.mDetachedTasks;
            set => this.mDetachedTasks = value;
        }

        public List<SharedVariable> Variables
        {
            get => this.mVariables;
            set => this.SetAllVariables(value);
        }

        public bool HasSerialized
        {
            get => this.mHasSerialized;
            set => this.mHasSerialized = value;
        }

        public TaskSerializationData TaskData
        {
            get => this.mTaskData;
            set => this.mTaskData = value;
        }

        public IBehavior Owner
        {
            get => this.mOwner;
            set => this.mOwner = value;
        }

        public void Initialize(IBehavior owner)
        {
            this.mOwner = owner;
        }

        public void Save(Task entryTask, Task rootTask, List<Task> detachedTasks)
        {
            this.mEntryTask = entryTask;
            this.mRootTask = rootTask;
            this.mDetachedTasks = detachedTasks;
        }

        public void Load(out Task entryTask, out Task rootTask, out List<Task> detachedTasks)
        {
            entryTask = this.mEntryTask;
            rootTask = this.mRootTask;
            detachedTasks = this.mDetachedTasks;
        }

        public bool CheckForSerialization(bool force, BehaviorSource behaviorSource = null, bool isPlaying = false)
        {
            if (this.mTaskData == null || this.HasSerialized && !force)
            {
                return false;
            }

            if (behaviorSource != null)
            {
                behaviorSource.HasSerialized = true;
            }

            this.HasSerialized = true;
            if (!string.IsNullOrEmpty(this.mTaskData.JSONSerialization))
            {
                JSONDeserialization.Load(this.mTaskData, behaviorSource != null ? behaviorSource : this, isPlaying || behaviorSource == null);
            }
            else
            {
                BinaryDeserialization.Load(this.mTaskData, behaviorSource != null ? behaviorSource : this, isPlaying || behaviorSource == null);
            }

            return true;
        }

        public SharedVariable GetVariable(string name)
        {
            if (name == null)
            {
                return (SharedVariable)null;
            }

            this.CheckForSerialization(false);
            if (this.mVariables != null)
            {
                if (this.mSharedVariableIndex == null || this.mSharedVariableIndex.Count != this.mVariables.Count)
                {
                    this.UpdateVariablesIndex();
                }

                int index;
                if (this.mSharedVariableIndex.TryGetValue(name, out index))
                {
                    return this.mVariables[index];
                }
            }

            return (SharedVariable)null;
        }

        public List<SharedVariable> GetAllVariables()
        {
            this.CheckForSerialization(false);
            return this.mVariables;
        }

        public void SetVariable(string name, SharedVariable sharedVariable)
        {
            if (this.mVariables == null)
            {
                this.mVariables = new List<SharedVariable>();
            }
            else if (this.mSharedVariableIndex == null || this.mSharedVariableIndex.Count != this.mVariables.Count)
            {
                this.UpdateVariablesIndex();
            }

            sharedVariable.Name = name;
            int index;
            if (this.mSharedVariableIndex != null && this.mSharedVariableIndex.TryGetValue(name, out index))
            {
                SharedVariable mVariable = this.mVariables[index];
                if (!mVariable.GetType().Equals(typeof(SharedVariable)) && !mVariable.GetType().Equals(sharedVariable.GetType()))
                {
                    Debug.LogError($"Error: Unable to set SharedVariable {(object)name} - the variable type {(object)mVariable.GetType()} does not match the existing type {(object)sharedVariable.GetType()}");
                }
                else if (!string.IsNullOrEmpty(sharedVariable.PropertyMapping))
                {
#if UNITY_PLATFORM
                    mVariable.PropertyMappingOwner = sharedVariable.PropertyMappingOwner;
#endif
                    mVariable.PropertyMapping = sharedVariable.PropertyMapping;
                    mVariable.InitializePropertyMapping(this);
                }
                else
                {
                    mVariable.SetValue(sharedVariable.GetValue());
                }
            }
            else
            {
                this.mVariables.Add(sharedVariable);
                this.UpdateVariablesIndex();
            }
        }

        public void UpdateVariableName(SharedVariable sharedVariable, string name)
        {
            this.CheckForSerialization(false);
            sharedVariable.Name = name;
            this.UpdateVariablesIndex();
        }

        public void SetAllVariables(List<SharedVariable> variables)
        {
            this.mVariables = variables;
            this.UpdateVariablesIndex();
        }

        private void UpdateVariablesIndex()
        {
            if (this.mVariables == null)
            {
                if (this.mSharedVariableIndex == null)
                {
                    return;
                }

                this.mSharedVariableIndex = (Dictionary<string, int>)null;
            }
            else
            {
                if (this.mSharedVariableIndex == null)
                {
                    this.mSharedVariableIndex = new Dictionary<string, int>(this.mVariables.Count);
                }
                else
                {
                    this.mSharedVariableIndex.Clear();
                }

                for (int index = 0; index < this.mVariables.Count; ++index)
                {
                    if (this.mVariables[index] != null)
                    {
                        this.mSharedVariableIndex.Add(this.mVariables[index].Name, index);
                    }
                }
            }
        }

        public override string ToString()
        {
            if (this.mOwner == null || this.mOwner.GetObject() == null)
            {
                return this.behaviorName;
            }

            return string.IsNullOrEmpty(this.behaviorName) ? this.Owner.GetOwnerName() : $"{(object)this.Owner.GetOwnerName()} - {(object)this.behaviorName}";
        }
    }
}