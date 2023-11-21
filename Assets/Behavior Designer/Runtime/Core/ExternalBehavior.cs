// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.ExternalBehavior
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections.Generic;
using BehaviorDesigner.Runtime.Tasks;
using SerializeField = Newtonsoft.Json.JsonPropertyAttribute;

namespace BehaviorDesigner.Runtime
{
    [Serializable]
    public abstract partial class ExternalBehavior : IBehavior
    {
      
#if !UNITY_EDITOR
        [SerializeField]
        private BehaviorSource mBehaviorSource;
#endif

        private bool mInitialized;

        // TODO externalBehaviorName需要在合适的时机，使用gameObject.name赋值
        private string externalBehaviorName;

        // TODO instanceID需要在合适的时机，this.GetInstanceID()赋值
        private int instanceID;

        public BehaviorSource BehaviorSource
        {
            get => this.mBehaviorSource;
            set => this.mBehaviorSource = value;
        }

        public string ExternalBehaviorObjName => this.externalBehaviorName;

        public bool Initialized => this.mInitialized;

        public BehaviorSource GetBehaviorSource()
        {
            return this.mBehaviorSource;
        }

        public void SetBehaviorSource(BehaviorSource behaviorSource)
        {
            this.mBehaviorSource = behaviorSource;
        }

#if !UNITY_EDITOR
        public System.Object GetObject()
        {
            return this;
        }
#endif

#if !UNITY_EDITOR
        public string GetOwnerName()
        {
            return this.externalBehaviorName;
        }
#endif

        public void Init()
        {
            this.CheckForSerialization();
            this.mInitialized = true;
        }

        public SharedVariable GetVariable(string name)
        {
            this.CheckForSerialization();
            return this.mBehaviorSource.GetVariable(name);
        }

        public void SetVariable(string name, SharedVariable item)
        {
            this.CheckForSerialization();
            this.mBehaviorSource.SetVariable(name, item);
        }

        public void SetVariableValue(string name, object value) =>
            this.GetVariable(name)?.SetValue(value);

        public T FindTask<T>()
            where T : Task
        {
            this.CheckForSerialization();
            return this.FindTask<T>(this.mBehaviorSource.RootTask);
        }

        private T FindTask<T>(Task task)
            where T : Task
        {
            if (task.GetType().Equals(typeof(T)))
            {
                return (T)task;
            }

            if (task is ParentTask parentTask && parentTask.Children != null)
            {
                for (int index = 0; index < parentTask.Children.Count; ++index)
                {
                    T task1 = this.FindTask<T>(parentTask.Children[index]);
                    if (task1 != null)
                    {
                        return task1;
                    }
                }
            }
            return (T)null;
        }

        public List<T> FindTasks<T>()
            where T : Task
        {
            this.CheckForSerialization();
            List<T> taskList = new List<T>();
            this.FindTasks<T>(this.mBehaviorSource.RootTask, ref taskList);
            return taskList;
        }

        private void FindTasks<T>(Task task, ref List<T> taskList)
            where T : Task
        {
            if (typeof(T).IsAssignableFrom(task.GetType()))
            {
                taskList.Add((T)task);
            }

            if (!(task is ParentTask parentTask) || parentTask.Children == null)
            {
                return;
            }

            for (int index = 0; index < parentTask.Children.Count; ++index)
            {
                this.FindTasks<T>(parentTask.Children[index], ref taskList);
            }
        }

        public Task FindTaskWithName(string taskName)
        {
            this.CheckForSerialization();
            return this.FindTaskWithName(taskName, this.mBehaviorSource.RootTask);
        }

        private void CheckForSerialization()
        {
            this.mBehaviorSource.Owner = (IBehavior)this;
            this.mBehaviorSource.CheckForSerialization(false);
        }

        private Task FindTaskWithName(string taskName, Task task)
        {
            if (task.FriendlyName.Equals(taskName))
            {
                return task;
            }

            if (task is ParentTask parentTask && parentTask.Children != null)
            {
                for (int index = 0; index < parentTask.Children.Count; ++index)
                {
                    Task taskWithName;
                    if (
                        (taskWithName = this.FindTaskWithName(taskName, parentTask.Children[index]))
                        != null
                    )
                    {
                        return taskWithName;
                    }
                }
            }
            return (Task)null;
        }

        public List<Task> FindTasksWithName(string taskName)
        {
            this.CheckForSerialization();
            List<Task> taskList = new List<Task>();
            this.FindTasksWithName(taskName, this.mBehaviorSource.RootTask, ref taskList);
            return taskList;
        }

        private void FindTasksWithName(string taskName, Task task, ref List<Task> taskList)
        {
            if (task.FriendlyName.Equals(taskName))
            {
                taskList.Add(task);
            }

            if (!(task is ParentTask parentTask) || parentTask.Children == null)
            {
                return;
            }

            for (int index = 0; index < parentTask.Children.Count; ++index)
            {
                this.FindTasksWithName(taskName, parentTask.Children[index], ref taskList);
            }
        }

#if !UNITY_EDITOR
        int IBehavior.GetInstanceID()
        {
            return this.instanceID;
        }
#endif
    }
}
