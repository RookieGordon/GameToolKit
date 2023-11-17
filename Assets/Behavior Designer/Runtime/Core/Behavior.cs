// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.Behavior
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections.Generic;
using System.Reflection;
using BehaviorDesigner.Runtime.Tasks;
using Newtonsoft.Json;
using Debug = BehaviorDesigner.Runtime.BehaviorDebug;

namespace BehaviorDesigner.Runtime
{
    [Serializable]
    public abstract partial class Behavior : IBehavior
    {
#if !UNITY_EDITOR
        [JsonProperty]
        private bool startWhenEnabled = true;

        [JsonProperty]
        private bool asynchronousLoad;

        [JsonProperty]
        private bool pauseWhenDisabled;

        [JsonProperty]
        private bool restartWhenComplete;

        [JsonProperty]
        private bool logTaskChanges;

        [JsonProperty]
        private int group;

        [JsonProperty]
        private bool resetValuesOnRestart;

        [JsonProperty]
        private ExternalBehavior externalBehavior;

        [JsonProperty]
        private BehaviorSource mBehaviorSource;
#endif
        private bool hasInheritedVariables;

        private bool isPaused;

        private TaskStatus executionStatus;

        private bool initialized;

        private Dictionary<Task, Dictionary<string, object>> defaultValues;

        private Dictionary<SharedVariable, object> defaultVariableValues;

        private bool[] hasEvent = new bool[12];

        private Dictionary<string, List<TaskCoroutine>> activeTaskCoroutines;

        private Dictionary<System.Type, Dictionary<string, Delegate>> eventTable;

        public bool showBehaviorDesignerGizmo = true;

        // TODO behaviorName需要在合适的时机，使用gameObject.name赋值
        private string behaviorName = string.Empty;

        public bool[] HasEvent => this.hasEvent;

        public event Behavior.BehaviorHandler OnBehaviorStart;

        public event Behavior.BehaviorHandler OnBehaviorRestart;

        public event Behavior.BehaviorHandler OnBehaviorEnd;

        // TODO instanceID需要在合适的时机，this.GetInstanceID()赋值
        public int instanceID;

        public Behavior()
        {
            this.mBehaviorSource = new BehaviorSource((IBehavior)this);
#if !UNITY_EDITOR
            this.OnEnable();
#endif
        }

        public bool StartWhenEnabled
        {
            get => this.startWhenEnabled;
            set => this.startWhenEnabled = value;
        }

        public bool AsynchronousLoad
        {
            get => this.asynchronousLoad;
            set => this.asynchronousLoad = value;
        }

        public bool PauseWhenDisabled
        {
            get => this.pauseWhenDisabled;
            set => this.pauseWhenDisabled = value;
        }

        public bool RestartWhenComplete
        {
            get => this.restartWhenComplete;
            set => this.restartWhenComplete = value;
        }

        public bool LogTaskChanges
        {
            get => this.logTaskChanges;
            set => this.logTaskChanges = value;
        }

        public int Group
        {
            get => this.group;
            set => this.group = value;
        }

        public bool ResetValuesOnRestart
        {
            get => this.resetValuesOnRestart;
            set => this.resetValuesOnRestart = value;
        }

        public ExternalBehavior ExternalBehavior
        {
            get => this.externalBehavior;
            set
            {
                if (this.externalBehavior == value)
                {
                    return;
                }
                if (BehaviorManager.instance != null)
                {
                    BehaviorManager.instance.DisableBehavior(this);
                }
                if (value != null && value.Initialized)
                {
                    List<SharedVariable> allVariables = this.mBehaviorSource.GetAllVariables();
                    this.mBehaviorSource = value.BehaviorSource;
                    this.mBehaviorSource.HasSerialized = true;
                    if (allVariables != null)
                    {
                        for (int index = 0; index < allVariables.Count; ++index)
                        {
                            if (allVariables[index] != null)
                            {
                                this.mBehaviorSource.SetVariable(
                                    allVariables[index].Name,
                                    allVariables[index]
                                );
                            }
                        }
                    }
                }
                else
                {
                    this.mBehaviorSource.HasSerialized = false;
                    this.hasInheritedVariables = false;
                }

                this.initialized = false;
                this.externalBehavior = value;
                if (!this.startWhenEnabled)
                {
                    return;
                }
                this.EnableBehavior();
            }
        }

        public bool HasInheritedVariables
        {
            get => this.hasInheritedVariables;
            set => this.hasInheritedVariables = value;
        }

        public string BehaviorName
        {
            get => this.mBehaviorSource.behaviorName;
            set => this.mBehaviorSource.behaviorName = value;
        }

        public string BehaviorDescription
        {
            get => this.mBehaviorSource.behaviorDescription;
            set => this.mBehaviorSource.behaviorDescription = value;
        }

        public TaskStatus ExecutionStatus
        {
            get => this.executionStatus;
            set => this.executionStatus = value;
        }

#if !UNITY_EDITOR
        int IBehavior.GetInstanceID()
        {
            return this.instanceID;
        }
#endif

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
            return (System.Object)this;
        }
#endif

#if !UNITY_EDITOR
        public string GetOwnerName()
        {
            return this.behaviorName;
        }
#endif

        public void Start()
        {
            if (!this.startWhenEnabled)
            {
                return;
            }
            this.EnableBehavior();
        }

        public async void EnableBehavior()
        {
            Behavior.CreateBehaviorManager();
            if (BehaviorManager.instance == null)
            {
                return;
            }
#if !UNITY_EDITOR
            await BehaviorManager.instance.EnableBehavior(this);
#else
            BehaviorManager.instance.EnableBehavior(this);
#endif
        }

        public void DisableBehavior()
        {
            if (BehaviorManager.instance == null)
            {
                return;
            }
            BehaviorManager.instance.DisableBehavior(this, this.pauseWhenDisabled);
            this.isPaused = this.pauseWhenDisabled;
        }

        public void DisableBehavior(bool pause)
        {
            if (BehaviorManager.instance == null)
            {
                return;
            }
            BehaviorManager.instance.DisableBehavior(this, pause);
            this.isPaused = pause;
        }

        public async void OnEnable()
        {
            this.SetUnityObject();
            if (BehaviorManager.instance == null || !this.isPaused)
            {
                if (!this.startWhenEnabled || !this.initialized)
                {
                    return;
                }
                this.EnableBehavior();
            }
            else
            {
#if !UNITY_EDITOR
                await BehaviorManager.instance.EnableBehavior(this);
#else
                BehaviorManager.instance.EnableBehavior(this);
#endif
                this.isPaused = false;
            }
        }

#if !UNITY_EDITOR
        public void SetUnityObject() { }
#endif

        // TODO 需要在合适的时机调用
        public void OnDisable()
        {
            this.DisableBehavior();
        }

        // TODO 需要在合适的时机调用
        public void OnDestroy()
        {
            if (BehaviorManager.instance != null)
            {
                BehaviorManager.instance.DestroyBehavior(this);
            }
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

        public void SetVariableValue(string name, object value)
        {
            SharedVariable variable = this.GetVariable(name);
            if (variable != null)
            {
                if (value is SharedVariable sharedVariable)
                {
                    if (!string.IsNullOrEmpty(sharedVariable.PropertyMapping))
                    {
                        variable.PropertyMapping = sharedVariable.PropertyMapping;
                        variable.PropertyMappingOwner = sharedVariable.PropertyMappingOwner;
                        variable.InitializePropertyMapping(this.mBehaviorSource);
                    }
                    else
                    {
                        variable.SetValue(sharedVariable.GetValue());
                    }
                }
                else
                {
                    variable.SetValue(value);
                }
            }
            else if (value is SharedVariable sharedVariable)
            {
                SharedVariable instance =
                    TaskUtility.CreateInstance(sharedVariable.GetType()) as SharedVariable;
                instance.Name = sharedVariable.Name;
                instance.IsShared = sharedVariable.IsShared;
                instance.IsGlobal = sharedVariable.IsGlobal;
                if (!string.IsNullOrEmpty(sharedVariable.PropertyMapping))
                {
                    instance.PropertyMapping = sharedVariable.PropertyMapping;
                    instance.PropertyMappingOwner = sharedVariable.PropertyMappingOwner;
                    instance.InitializePropertyMapping(this.mBehaviorSource);
                }
                else
                {
                    instance.SetValue(sharedVariable.GetValue());
                }

                this.mBehaviorSource.SetVariable(name, instance);
            }
            else
            {
                Debug.LogError($"Error: No variable exists with name {name}");
            }
        }

        public List<SharedVariable> GetAllVariables()
        {
            this.CheckForSerialization();
            return this.mBehaviorSource.GetAllVariables();
        }

#if !UNITY_EDITOR
        public void CheckForSerialization()
        {
            this.CheckForSerialization(false, true);
        }
#endif

        public void CheckForSerialization(bool forceSerialization, bool isPlaying)
        {
            if (this.externalBehavior != null)
            {
                bool hasSerialized = this.mBehaviorSource.HasSerialized;
                this.mBehaviorSource.CheckForSerialization(
                    forceSerialization || !hasSerialized,
                    isPlaying: isPlaying
                );
                List<SharedVariable> allVariables = this.mBehaviorSource.GetAllVariables();
                this.hasInheritedVariables = allVariables != null && allVariables.Count > 0;
                this.externalBehavior.BehaviorSource.Owner = (IBehavior)this.ExternalBehavior;
                this.externalBehavior
                    .BehaviorSource
                    .CheckForSerialization(
                        forceSerialization || !hasSerialized,
                        this.GetBehaviorSource(),
                        isPlaying
                    );
                this.externalBehavior.BehaviorSource.EntryTask = this.mBehaviorSource.EntryTask;
                if (!this.hasInheritedVariables)
                {
                    return;
                }
                for (int index = 0; index < allVariables.Count; ++index)
                {
                    if (allVariables[index] != null)
                    {
                        this.mBehaviorSource.SetVariable(
                            allVariables[index].Name,
                            allVariables[index]
                        );
                    }
                }
            }
            else
            {
                this.mBehaviorSource.CheckForSerialization(false, isPlaying: isPlaying);
            }
        }

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
            if (task is ParentTask parentTask && parentTask.Children != null)
            {
                for (int index = 0; index < parentTask.Children.Count; ++index)
                {
                    this.FindTasks<T>(parentTask.Children[index], ref taskList);
                }
            }
        }

        public Task FindTaskWithName(string taskName)
        {
            this.CheckForSerialization();
            return this.FindTaskWithName(taskName, this.mBehaviorSource.RootTask);
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
                    Task taskWithName = this.FindTaskWithName(taskName, parentTask.Children[index]);
                    if (taskWithName != null)
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
            if (task is ParentTask parentTask && parentTask.Children != null)
            {
                for (int index = 0; index < parentTask.Children.Count; ++index)
                {
                    this.FindTasksWithName(taskName, parentTask.Children[index], ref taskList);
                }
            }
        }

        public List<Task> GetActiveTasks()
        {
            return BehaviorManager.instance == null
                ? (List<Task>)null
                : BehaviorManager.instance.GetActiveTasks(this);
        }

        public void OnBehaviorStarted()
        {
            if (!this.initialized)
            {
                this.CheckForEventMethods(new HashSet<Task>(), this.mBehaviorSource.RootTask);
                this.initialized = true;
            }

            if (this.OnBehaviorStart == null)
            {
                return;
            }
            this.OnBehaviorStart(this);
        }

        private void CheckForEventMethods(HashSet<Task> tasksChecked, Task task)
        {
            if (task == null)
            {
                return;
            }
            if (!tasksChecked.Contains(task))
            {
                MethodInfo[] methods = task.GetType()
                    .GetMethods(
                        BindingFlags.DeclaredOnly
                            | BindingFlags.Instance
                            | BindingFlags.Public
                            | BindingFlags.NonPublic
                    );
                if (methods != null)
                {
                    for (int index1 = 0; index1 < 12; ++index1)
                    {
                        if (!this.hasEvent[index1])
                        {
                            string str = ((Behavior.EventTypes)index1).ToString();
                            for (int index2 = 0; index2 < methods.Length; ++index2)
                            {
                                this.hasEvent[index1] = methods[index2].Name.Equals(str);
                                if (this.hasEvent[index1])
                                    break;
                            }
                        }
                    }
                }
            }

            if (task is ParentTask parentTask && parentTask.Children != null)
            {
                for (int index = 0; index < parentTask.Children.Count; ++index)
                {
                    this.CheckForEventMethods(tasksChecked, parentTask.Children[index]);
                }
            }
        }

        public void OnBehaviorRestarted()
        {
            if (this.OnBehaviorRestart == null)
            {
                return;
            }
            this.OnBehaviorRestart(this);
        }

        public void OnBehaviorEnded()
        {
            if (this.OnBehaviorEnd == null)
            {
                return;
            }
            this.OnBehaviorEnd(this);
        }

        private void RegisterEvent(string name, Delegate handler)
        {
            if (this.eventTable == null)
            {
                this.eventTable = new Dictionary<System.Type, Dictionary<string, Delegate>>();
            }
            Dictionary<string, Delegate> dictionary;
            if (!this.eventTable.TryGetValue(handler.GetType(), out dictionary))
            {
                dictionary = new Dictionary<string, Delegate>();
                this.eventTable.Add(handler.GetType(), dictionary);
            }

            Delegate a;
            if (dictionary.TryGetValue(name, out a))
            {
                dictionary[name] = Delegate.Combine(a, handler);
            }
            else
            {
                dictionary.Add(name, handler);
            }
        }

        public void RegisterEvent(string name, System.Action handler)
        {
            this.RegisterEvent(name, (Delegate)handler);
        }

        public void RegisterEvent<T>(string name, System.Action<T> handler)
        {
            this.RegisterEvent(name, (Delegate)handler);
        }

        public void RegisterEvent<T, U>(string name, System.Action<T, U> handler)
        {
            this.RegisterEvent(name, (Delegate)handler);
        }

        public void RegisterEvent<T, U, V>(string name, System.Action<T, U, V> handler)
        {
            this.RegisterEvent(name, (Delegate)handler);
        }

        private Delegate GetDelegate(string name, System.Type type)
        {
            Dictionary<string, Delegate> dictionary;
            Delegate @delegate;
            return
                this.eventTable != null
                && this.eventTable.TryGetValue(type, out dictionary)
                && dictionary.TryGetValue(name, out @delegate)
                ? @delegate
                : (Delegate)null;
        }

        public void SendEvent(string name)
        {
            if (!(this.GetDelegate(name, typeof(System.Action)) is System.Action action))
            {
                return;
            }
            action();
        }

        public void SendEvent<T>(string name, T arg1)
        {
            if (!(this.GetDelegate(name, typeof(System.Action<T>)) is System.Action<T> action))
            {
                return;
            }
            action(arg1);
        }

        public void SendEvent<T, U>(string name, T arg1, U arg2)
        {
            if (
                !(this.GetDelegate(name, typeof(System.Action<T, U>)) is System.Action<T, U> action)
            )
            {
                return;
            }
            action(arg1, arg2);
        }

        public void SendEvent<T, U, V>(string name, T arg1, U arg2, V arg3)
        {
            if (
                !(
                    this.GetDelegate(name, typeof(System.Action<T, U, V>))
                    is System.Action<T, U, V> action
                )
            )
            {
                return;
            }
            action(arg1, arg2, arg3);
        }

        private void UnregisterEvent(string name, Delegate handler)
        {
            Dictionary<string, Delegate> dictionary;
            Delegate source;
            if (
                this.eventTable == null
                || !this.eventTable.TryGetValue(handler.GetType(), out dictionary)
                || !dictionary.TryGetValue(name, out source)
            )
            {
                return;
            }
            dictionary[name] = Delegate.Remove(source, handler);
        }

        public void UnregisterEvent(string name, System.Action handler)
        {
            this.UnregisterEvent(name, (Delegate)handler);
        }

        public void UnregisterEvent<T>(string name, System.Action<T> handler)
        {
            this.UnregisterEvent(name, (Delegate)handler);
        }

        public void UnregisterEvent<T, U>(string name, System.Action<T, U> handler)
        {
            this.UnregisterEvent(name, (Delegate)handler);
        }

        public void UnregisterEvent<T, U, V>(string name, System.Action<T, U, V> handler)
        {
            this.UnregisterEvent(name, (Delegate)handler);
        }

        public void SaveResetValues()
        {
            if (this.defaultValues == null)
            {
                this.CheckForSerialization();
                this.defaultValues = new Dictionary<Task, Dictionary<string, object>>();
                this.defaultVariableValues = new Dictionary<SharedVariable, object>();
                this.SaveValues();
            }
            else
            {
                this.ResetValues();
            }
        }

        private void SaveValues()
        {
            List<SharedVariable> allVariables = this.mBehaviorSource.GetAllVariables();
            if (allVariables != null)
            {
                for (int index = 0; index < allVariables.Count; ++index)
                {
                    this.defaultVariableValues.Add(
                        allVariables[index],
                        allVariables[index].GetValue()
                    );
                }
            }

            this.SaveValue(this.mBehaviorSource.RootTask);
        }

        private void SaveValue(Task task)
        {
            if (task == null)
            {
                return;
            }
            FieldInfo[] publicFields = TaskUtility.GetPublicFields(task.GetType());
            Dictionary<string, object> dictionary = new Dictionary<string, object>();
            for (int index = 0; index < publicFields.Length; ++index)
            {
                object obj = publicFields[index].GetValue((object)task);
                if (obj is SharedVariable)
                {
                    SharedVariable sharedVariable = obj as SharedVariable;
                    if (sharedVariable.IsGlobal || sharedVariable.IsShared)
                    {
                        continue;
                    }
                }

                dictionary.Add(
                    publicFields[index].Name,
                    publicFields[index].GetValue((object)task)
                );
            }

            this.defaultValues.Add(task, dictionary);
            if (!(task is ParentTask))
            {
                return;
            }
            ParentTask parentTask = task as ParentTask;
            if (parentTask.Children == null)
            {
                return;
            }
            for (int index = 0; index < parentTask.Children.Count; ++index)
            {
                this.SaveValue(parentTask.Children[index]);
            }
        }

        private void ResetValues()
        {
            foreach (
                KeyValuePair<
                    SharedVariable,
                    object
                > defaultVariableValue in this.defaultVariableValues
            )
            {
                this.SetVariableValue(defaultVariableValue.Key.Name, defaultVariableValue.Value);
            }
            this.ResetValue(this.mBehaviorSource.RootTask);
        }

        private void ResetValue(Task task)
        {
            Dictionary<string, object> dictionary;
            if (task == null || !this.defaultValues.TryGetValue(task, out dictionary))
            {
                return;
            }
            foreach (KeyValuePair<string, object> keyValuePair in dictionary)
            {
                FieldInfo field = task.GetType().GetField(keyValuePair.Key);
                if (field != (FieldInfo)null)
                {
                    field.SetValue((object)task, keyValuePair.Value);
                }
            }

            if (!(task is ParentTask))
            {
                return;
            }
            ParentTask parentTask = task as ParentTask;
            if (parentTask.Children == null)
            {
                return;
            }
            for (int index = 0; index < parentTask.Children.Count; ++index)
            {
                this.ResetValue(parentTask.Children[index]);
            }
        }

#if !UNITY_EDITOR
        public override string ToString()
        {
            return this.mBehaviorSource.ToString();
        }
#endif

        public enum EventTypes
        {
            OnCollisionEnter,
            OnCollisionExit,
            OnTriggerEnter,
            OnTriggerExit,
            OnCollisionEnter2D,
            OnCollisionExit2D,
            OnTriggerEnter2D,
            OnTriggerExit2D,
            OnControllerColliderHit,
            OnLateUpdate,
            OnFixedUpdate,
            OnAnimatorIK,
            None,
        }

        public delegate void BehaviorHandler(Behavior behavior);

        public enum GizmoViewMode
        {
            Running,
            Always,
            Selected,
            Never,
        }
    }
}
