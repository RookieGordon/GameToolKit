// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.Tasks.ParentTask
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System.Collections.Generic;
using Newtonsoft.Json;

namespace BehaviorDesigner.Runtime.Tasks
{
    public abstract partial class ParentTask : Task
    {
#if !UNITY_PLATFORM
        [JsonProperty] protected List<Task> children;
#endif

        public List<Task> Children
        {
            get => this.children;
            private set => this.children = value;
        }

        public virtual int MaxChildren() => int.MaxValue;

        public virtual bool CanRunParallelChildren() => false;

        public virtual int CurrentChildIndex() => 0;

        public virtual bool CanExecute() => true;

        public virtual TaskStatus Decorate(TaskStatus status) => status;

        public virtual bool CanReevaluate() => false;

        public virtual void OnChildExecuted(TaskStatus childStatus)
        {
        }

        public virtual void OnChildExecuted(int childIndex, TaskStatus childStatus)
        {
        }

        public virtual void OnChildStarted()
        {
        }

        public virtual void OnChildStarted(int childIndex)
        {
        }

        public virtual TaskStatus OverrideStatus(TaskStatus status)
        {
            return status;
        }

        public virtual TaskStatus OverrideStatus()
        {
            return TaskStatus.Running;
        }

        public virtual void OnConditionalAbort(int childIndex)
        {
        }

        public override float GetUtility()
        {
            float utility = 0.0f;
            if (this.children != null)
            {
                for (int index = 0; index < this.children.Count; ++index)
                {
                    if (this.children[index] != null && !this.children[index].Disabled)
                    {
                        utility += this.children[index].GetUtility();
                    }
                }
            }

            return utility;
        }

        public override void OnDrawGizmos()
        {
            if (this.children == null)
            {
                return;
            }

            for (int index = 0; index < this.children.Count; ++index)
            {
                if (this.children[index] != null && !this.children[index].Disabled)
                {
                    this.children[index].OnDrawGizmos();
                }
            }
        }

        public void AddChild(Task child, int index)
        {
            if (this.children == null)
            {
                this.children = new List<Task>();
            }

            this.children.Insert(index, child);
        }

        public void ReplaceAddChild(Task child, int index)
        {
            if (this.children != null && index < this.children.Count)
            {
                this.children[index] = child;
            }
            else
            {
                this.AddChild(child, index);
            }
        }
    }
}