// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.BehaviorManager
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using BehaviorDesigner.Runtime.Tasks;
using Newtonsoft.Json;
using Unity.Mathematics;
using CSTask = System.Threading.Tasks.Task;
using Debug = BehaviorDesigner.Runtime.BehaviorDebug;
using Task = BehaviorDesigner.Runtime.Tasks.Task;

namespace BehaviorDesigner.Runtime
{
    [JsonIgnoreBase]
    public partial class BehaviorManager
    {
        public static BehaviorManager instance;

#if !UNITY_PLATFORM
        [JsonProperty]
        private UpdateIntervalType updateInterval;

        [JsonProperty]
        private float updateIntervalSeconds;

        [JsonProperty]
        private BehaviorManager.ExecutionsPerTickType executionsPerTick;

        [JsonProperty]
        private int maxTaskExecutionsPerTick = 100;
#endif

        public BehaviorManager.BehaviorManagerHandler onEnableBehavior;

        public BehaviorManager.BehaviorManagerHandler onTaskBreakpoint;

        private static bool isPlaying;

#if !UNITY_PLATFORM
        private System.Object lockObject = new System.Object();
        
        private List<BehaviorLoaderTask> behaviorLoaders = new List<BehaviorLoaderTask>();
#endif

        private List<BehaviorManager.BehaviorTree> behaviorTrees =
            new List<BehaviorManager.BehaviorTree>();

        private Dictionary<Behavior, BehaviorManager.BehaviorTree> pausedBehaviorTrees =
            new Dictionary<Behavior, BehaviorManager.BehaviorTree>();

        private Dictionary<Behavior, BehaviorManager.BehaviorTree> behaviorTreeMap =
            new Dictionary<Behavior, BehaviorManager.BehaviorTree>();

        private List<int> conditionalParentIndexes = new List<int>();

        private Dictionary<object, BehaviorManager.ThirdPartyTask> objectTaskMap =
            new Dictionary<object, BehaviorManager.ThirdPartyTask>();

        private Dictionary<BehaviorManager.ThirdPartyTask, object> taskObjectMap = new Dictionary<
            BehaviorManager.ThirdPartyTask,
            object
        >((IEqualityComparer<BehaviorManager.ThirdPartyTask>)
            new BehaviorManager.ThirdPartyTaskComparer());

        private BehaviorManager.ThirdPartyTask thirdPartyTaskCompare =
            new BehaviorManager.ThirdPartyTask();

        private static MethodInfo playMakerStopMethod;

        private static MethodInfo uScriptStopMethod;

        private static MethodInfo dialogueSystemStopMethod;

        private static MethodInfo uSequencerStopMethod;

        private static object[] invokeParameters;

        private Behavior breakpointTree;

        private bool dirty;
        
#if !UNITY_PLATFORM
        public bool enabled = true;
#endif

        public UpdateIntervalType UpdateInterval
        {
            get => this.updateInterval;
            set
            {
                this.updateInterval = value;
                this.UpdateIntervalChanged();
            }
        }

        public float UpdateIntervalSeconds
        {
            get => this.updateIntervalSeconds;
            set
            {
                this.updateIntervalSeconds = value;
                this.UpdateIntervalChanged();
            }
        }

        public BehaviorManager.ExecutionsPerTickType ExecutionsPerTick
        {
            get => this.executionsPerTick;
            set => this.executionsPerTick = value;
        }

        public int MaxTaskExecutionsPerTick
        {
            get => this.maxTaskExecutionsPerTick;
            set => this.maxTaskExecutionsPerTick = value;
        }

        public BehaviorManager.BehaviorManagerHandler OnEnableBehavior
        {
            set => this.onEnableBehavior = value;
        }

        public BehaviorManager.BehaviorManagerHandler OnTaskBreakpoint
        {
            get => this.onTaskBreakpoint;
            set => this.onTaskBreakpoint += value;
        }

        public static bool IsPlaying
        {
            get => BehaviorManager.isPlaying;
            set => BehaviorManager.isPlaying = value;
        }

        public List<BehaviorManager.BehaviorTree> BehaviorTrees => this.behaviorTrees;

        private static MethodInfo PlayMakerStopMethod
        {
            get
            {
                if (BehaviorManager.playMakerStopMethod == (MethodInfo)null)
                    BehaviorManager.playMakerStopMethod = TaskUtility
                        .GetTypeWithinAssembly("BehaviorDesigner.Runtime.BehaviorManager_PlayMaker")
                        .GetMethod("StopPlayMaker");
                return BehaviorManager.playMakerStopMethod;
            }
        }

        private static MethodInfo UScriptStopMethod
        {
            get
            {
                if (BehaviorManager.uScriptStopMethod == (MethodInfo)null)
                    BehaviorManager.uScriptStopMethod = TaskUtility
                        .GetTypeWithinAssembly("BehaviorDesigner.Runtime.BehaviorManager_uScript")
                        .GetMethod("StopuScript");
                return BehaviorManager.uScriptStopMethod;
            }
        }

        private static MethodInfo DialogueSystemStopMethod
        {
            get
            {
                if (BehaviorManager.dialogueSystemStopMethod == (MethodInfo)null)
                    BehaviorManager.dialogueSystemStopMethod = TaskUtility
                        .GetTypeWithinAssembly("BehaviorDesigner.Runtime.BehaviorManager_DialogueSystem")
                        .GetMethod("StopDialogueSystem");
                return BehaviorManager.dialogueSystemStopMethod;
            }
        }

        private static MethodInfo USequencerStopMethod
        {
            get
            {
                if (BehaviorManager.uSequencerStopMethod == (MethodInfo)null)
                    BehaviorManager.uSequencerStopMethod = TaskUtility
                        .GetTypeWithinAssembly("BehaviorDesigner.Runtime.BehaviorManager_uSequencer")
                        .GetMethod("StopuSequencer");
                return BehaviorManager.uSequencerStopMethod;
            }
        }

        public Behavior BreakpointTree
        {
            get => this.breakpointTree;
            set => this.breakpointTree = value;
        }

        public bool Dirty
        {
            get => this.dirty;
            set => this.dirty = value;
        }

#if !UNITY_PLATFORM
        public BehaviorManager()
        {
            this.Awake();
        }
#endif

#if !UNITY_PLATFORM
        public async void Awake()
        {
            BehaviorManager.instance = this;
            BehaviorManager.isPlaying = true;
            await this.UpdateIntervalChanged();
        }
#endif

#if !UNITY_PLATFORM
        private async CSTask UpdateIntervalChanged()
        {
            // 等待0.017秒
            await CSTask.Delay((int)this.updateIntervalSeconds);
            if (this.updateInterval == UpdateIntervalType.EveryFrame)
            {
                this.enabled = true;
            }
            else if (this.updateInterval == UpdateIntervalType.SpecifySeconds)
            {
                await CSTask.Delay((int)this.updateIntervalSeconds);
                this.enabled = false;
            }
            else
            {
                this.enabled = false;
            }
        }
#endif

        //TODO 找个地方调用一下
        public void OnDestroy()
        {
            for (int index = this.behaviorTrees.Count - 1; index > -1; --index)
            {
                this.DisableBehavior(this.behaviorTrees[index].behavior);
            }

            ObjectPool.Clear();
            BehaviorManager.instance = null;
            BehaviorManager.isPlaying = false;
        }

#if !UNITY_PLATFORM
        public async CSTask EnableBehavior(Behavior behavior)
        {
            if (this.IsBehaviorEnabled(behavior))
            {
                return;
            }

            if (this.pausedBehaviorTrees.TryGetValue(behavior, out var behaviorTree))
            {
                this.behaviorTrees.Add(behaviorTree);
                this.pausedBehaviorTrees.Remove(behavior);
                behavior.ExecutionStatus = TaskStatus.Running;
                for (int index = 0; index < behaviorTree.taskList.Count; ++index)
                {
                    behaviorTree.taskList[index].OnPause(false);
                }
            }
            else if (behavior.AsynchronousLoad)
            {
                var loader = new Func<Behavior, BehaviorTree>(this.LoadBehavior);
                var loadSuccess = new Action<Behavior, BehaviorTree>(this.LoadBehaviorComplete);
                var loadTask = new BehaviorLoaderTask(behavior, loader, loadSuccess, (BehaviorLoaderTask loadTask) => { this.behaviorLoaders.Remove(loadTask); });
                this.behaviorLoaders.Add(loadTask);
                await loadTask.LoaderTask;
            }
            else
            {
                behaviorTree = this.LoadBehavior(behavior);
                this.LoadBehaviorComplete(behavior, behaviorTree);
            }
        }
#endif

#if !UNITY_PLATFORM
        private BehaviorManager.BehaviorTree LoadBehavior(Behavior behavior)
        {
            BehaviorManager.BehaviorTree behaviorTree =
                ObjectPool.Get<BehaviorManager.BehaviorTree>();
            lock ((object)this.lockObject)
            {
                behavior.CheckForSerialization(false, true);
            }
            Task rootTask = behavior.GetBehaviorSource().RootTask;
            if (rootTask == null)
            {
                behaviorTree.errorState =
                    $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{behavior.BehaviorObjectName}\" contains no root task. This behavior will be disabled.";
                return behaviorTree;
            }
            behaviorTree.Initialize(behavior);
            behaviorTree.parentIndex.Add(-1);
            behaviorTree.relativeChildIndex.Add(-1);
            behaviorTree.parentCompositeIndex.Add(-1);
            BehaviorManager.TaskAddData data = ObjectPool.Get<BehaviorManager.TaskAddData>();
            data.Initialize();
            bool hasExternalBehavior = behavior.ExternalBehavior != null;
            int taskList = this.AddToTaskList(
                behaviorTree,
                rootTask,
                ref hasExternalBehavior,
                data
            );
            if (taskList < 0)
            {
                switch (taskList + 6)
                {
                    case 0:
                        behaviorTree.errorState =
                            $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{behavior.BehaviorObjectName}\" contains a root task which is disabled. This behavior will be disabled.";
                        break;
                    case 1:
                        behaviorTree.errorState =
                            $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{behavior.BehaviorObjectName}\" contains a Behavior Tree Reference task ({(object)data.errorTaskName} (index {(object)data.errorTask})) that which has an element with a null value in the externalBehaviors array. This behavior will be disabled.";
                        break;
                    case 2:
                        behaviorTree.errorState =
                            $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{behavior.BehaviorObjectName}\" contains multiple external behavior trees at the root task or as a child of a parent task which cannot contain so many children (such as a decorator task). This behavior will be disabled.";
                        break;
                    case 3:
                        behaviorTree.errorState =
                            $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{behavior.BehaviorObjectName}\" contains a null task (referenced from parent task {(object)data.errorTaskName} (index {(object)data.errorTask})). This behavior will be disabled.";
                        break;
                    case 4:
                        behaviorTree.errorState =
                            $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{behavior.BehaviorObjectName}\" cannot find the referenced external task. This behavior will be disabled.";
                        break;
                    case 5:
                        behaviorTree.errorState =
                            $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{behavior.BehaviorObjectName}\" contains a parent task ({(object)data.errorTaskName} (index {(object)data.errorTask})) with no children. This behavior will be disabled.";
                        break;
                }
            }
            data.Destroy();
            ObjectPool.Return<BehaviorManager.TaskAddData>(data);
            return behaviorTree;
        }
#endif

        private void LoadBehaviorComplete(Behavior behavior, BehaviorTree behaviorTree)
        {
            if (behavior == null || behaviorTree == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(behaviorTree.errorState))
            {
                Debug.LogError(behaviorTree.errorState);
            }
            else
            {
                this.dirty = true;
                if (behavior.ExternalBehavior != null)
                    behavior.GetBehaviorSource().EntryTask = behavior
                        .ExternalBehavior
                        .BehaviorSource
                        .EntryTask;
                behavior.GetBehaviorSource().RootTask = behaviorTree.taskList[0];
                if (behavior.ResetValuesOnRestart)
                {
                    behavior.SaveResetValues();
                }

                Stack<int> intStack = ObjectPool.Get<Stack<int>>();
                intStack.Clear();
                behaviorTree.activeStack.Add(intStack);
                behaviorTree.interruptionIndex.Add(-1);
                behaviorTree.interruptionTaskStatus.Add(TaskStatus.Failure);
                behaviorTree.nonInstantTaskStatus.Add(TaskStatus.Inactive);
                if (behaviorTree.behavior.LogTaskChanges)
                {
                    for (int index = 0; index < behaviorTree.taskList.Count; ++index)
                        Debug.Log($"{this.RoundedTime()}: Task {(object)behaviorTree.taskList[index].FriendlyName} ({(object)behaviorTree.taskList[index].GetType()}, index {(object)index}) {(object)behaviorTree.taskList[index].GetHashCode()}");
                }

                for (int index = 0; index < behaviorTree.taskList.Count; ++index)
                {
                    behaviorTree.taskList[index].OnAwake();
                }

                this.behaviorTrees.Add(behaviorTree);
                this.behaviorTreeMap.Add(behavior, behaviorTree);
                if (this.onEnableBehavior != null)
                {
                    this.onEnableBehavior();
                }

                if (behaviorTree.taskList[0].Disabled)
                {
                    return;
                }

                behavior.OnBehaviorStarted();
                behavior.ExecutionStatus = TaskStatus.Running;
                this.PushTask(behaviorTree, 0, 0);
            }
        }

#if !UNITY_PLATFORM
        private int AddToTaskList(
            BehaviorTree behaviorTree,
            Task task,
            ref bool hasExternalBehavior,
            TaskAddData data
        )
        {
            if (task == null)
            {
                return -3;
            }
            task.Owner = behaviorTree.behavior;
            if (task is BehaviorReference)
            {
                if (!(task is BehaviorReference behaviorReference))
                {
                    return -2;
                }
                ExternalBehavior[] externalBehaviors;
                if ((externalBehaviors = behaviorReference.GetExternalBehaviors()) == null)
                {
                    return -2;
                }
                BehaviorSource[] behaviorSourceArray = new BehaviorSource[externalBehaviors.Length];
                for (int index = 0; index < externalBehaviors.Length; ++index)
                {
                    if (externalBehaviors[index] == null)
                    {
                        data.errorTask = behaviorTree.taskList.Count;
                        data.errorTaskName = string.IsNullOrEmpty(task.FriendlyName)
                            ? task.GetType().ToString()
                            : task.FriendlyName;
                        return -5;
                    }
                    behaviorSourceArray[index] = externalBehaviors[index].BehaviorSource;
                    behaviorSourceArray[index].Owner = (IBehavior)externalBehaviors[index];
                }
                if (behaviorSourceArray == null)
                {
                    return -2;
                }
                ParentTask parentTask = data.parentTask;
                int parentIndex = data.parentIndex;
                int compositeParentIndex = data.compositeParentIndex;
                data.offset = task.NodeData.Offset;
                ++data.depth;
                for (int index1 = 0; index1 < behaviorSourceArray.Length; ++index1)
                {
                    BehaviorSource behaviorSource = ObjectPool.Get<BehaviorSource>();
                    behaviorSource.Initialize(behaviorSourceArray[index1].Owner);
                    lock ((object)this.lockObject)
                        behaviorSourceArray[index1].CheckForSerialization(
                            true,
                            behaviorSource,
                            true
                        );
                    Task rootTask = behaviorSource.RootTask;
                    if (rootTask != null)
                    {
                        if (rootTask is ParentTask)
                            rootTask.NodeData.Collapsed = (task as BehaviorReference).collapsed;
                        rootTask.Disabled = task.Disabled;
                        if (behaviorReference.variables != null)
                        {
                            for (
                                int index2 = 0;
                                index2 < behaviorReference.variables.Length;
                                ++index2
                            )
                            {
                                if (behaviorReference.variables[index2] != null)
                                {
                                    if (data.overrideFields == null)
                                    {
                                        data.overrideFields = ObjectPool.Get<
                                            Dictionary<
                                                string,
                                                BehaviorManager.TaskAddData.OverrideFieldValue
                                            >
                                        >();
                                        data.overrideFields.Clear();
                                    }
                                    if (
                                        !data.overrideFields.ContainsKey(
                                            behaviorReference.variables[index2].Value.name
                                        )
                                    )
                                    {
                                        BehaviorManager.TaskAddData.OverrideFieldValue overrideFieldValue1 =
                                            ObjectPool.Get<BehaviorManager.TaskAddData.OverrideFieldValue>();
                                        overrideFieldValue1.Initialize(
                                            (object)behaviorReference.variables[index2].Value,
                                            data.depth
                                        );
                                        if (behaviorReference.variables[index2].Value != null)
                                        {
                                            NamedVariable namedVariable = behaviorReference
                                                .variables[index2]
                                                .Value;
                                            if (string.IsNullOrEmpty(namedVariable.name))
                                            {
                                                UnityEngine
                                                    .Debug
                                                    .LogWarning(
                                                        $"Warning: Named variable on reference task {behaviorReference.FriendlyName} (id {(object)behaviorReference.ID}) is null"
                                                    );
                                                continue;
                                            }
                                            if (namedVariable.value != null)
                                            {
                                                BehaviorManager.TaskAddData.OverrideFieldValue overrideFieldValue2;
                                                if (
                                                    data.overrideFields.TryGetValue(
                                                        namedVariable.name,
                                                        out overrideFieldValue2
                                                    )
                                                )
                                                    overrideFieldValue1 = overrideFieldValue2;
                                                else if (
                                                    !string.IsNullOrEmpty(namedVariable.value.Name)
                                                    && data.overrideFields.TryGetValue(
                                                        namedVariable.value.Name,
                                                        out overrideFieldValue2
                                                    )
                                                )
                                                    overrideFieldValue1 = overrideFieldValue2;
                                            }
                                        }
                                        else if (behaviorReference.variables[index2].Value != null)
                                        {
                                            GenericVariable genericVariable = (GenericVariable)
                                                behaviorReference.variables[index2].Value;
                                            if (genericVariable.value != null)
                                            {
                                                if (
                                                    string.IsNullOrEmpty(genericVariable.value.Name)
                                                )
                                                {
                                                    UnityEngine
                                                        .Debug
                                                        .LogWarning(
                                                            $"Warning: Named variable on reference task {behaviorReference.FriendlyName} (id {(object)behaviorReference.ID}) is null"
                                                        );
                                                    continue;
                                                }
                                                BehaviorManager.TaskAddData.OverrideFieldValue overrideFieldValue3;
                                                if (
                                                    data.overrideFields.TryGetValue(
                                                        genericVariable.value.Name,
                                                        out overrideFieldValue3
                                                    )
                                                )
                                                    overrideFieldValue1 = overrideFieldValue3;
                                            }
                                        }
                                        data.overrideFields.Add(
                                            behaviorReference.variables[index2].Value.name,
                                            overrideFieldValue1
                                        );
                                    }
                                }
                            }
                        }
                        if (behaviorSource.Variables != null)
                        {
                            for (int index3 = 0; index3 < behaviorSource.Variables.Count; ++index3)
                            {
                                if (behaviorSource.Variables[index3] != null)
                                {
                                    SharedVariable variable;
                                    if (
                                        (
                                            variable = behaviorTree
                                                .behavior
                                                .GetVariable(behaviorSource.Variables[index3].Name)
                                        ) == null
                                    )
                                    {
                                        variable = behaviorSource.Variables[index3];
                                        behaviorTree.behavior.SetVariable(variable.Name, variable);
                                    }
                                    else
                                        behaviorSource
                                            .Variables[index3]
                                            .SetValue(variable.GetValue());
                                    if (data.overrideFields == null)
                                    {
                                        data.overrideFields = ObjectPool.Get<
                                            Dictionary<
                                                string,
                                                BehaviorManager.TaskAddData.OverrideFieldValue
                                            >
                                        >();
                                        data.overrideFields.Clear();
                                    }
                                    if (!data.overrideFields.ContainsKey(variable.Name))
                                    {
                                        BehaviorManager.TaskAddData.OverrideFieldValue overrideFieldValue =
                                            ObjectPool.Get<BehaviorManager.TaskAddData.OverrideFieldValue>();
                                        overrideFieldValue.Initialize((object)variable, data.depth);
                                        data.overrideFields.Add(variable.Name, overrideFieldValue);
                                    }
                                }
                            }
                        }
                        ObjectPool.Return<BehaviorSource>(behaviorSource);
                        if (index1 > 0)
                        {
                            data.parentTask = parentTask;
                            data.parentIndex = parentIndex;
                            data.compositeParentIndex = compositeParentIndex;
                            if (data.parentTask == null || index1 >= data.parentTask.MaxChildren())
                            {
                                return -4;
                            }
                            behaviorTree.parentIndex.Add(data.parentIndex);
                            behaviorTree.relativeChildIndex.Add(data.parentTask.Children.Count);
                            behaviorTree.parentCompositeIndex.Add(data.compositeParentIndex);
                            behaviorTree
                                .childrenIndex[data.parentIndex]
                                .Add(behaviorTree.taskList.Count);
                            data.parentTask.AddChild(rootTask, data.parentTask.Children.Count);
                        }
                        hasExternalBehavior = true;
                        bool fromExternalTask = data.fromExternalTask;
                        data.fromExternalTask = true;
                        int taskList;
                        if (
                            (
                                taskList = this.AddToTaskList(
                                    behaviorTree,
                                    rootTask,
                                    ref hasExternalBehavior,
                                    data
                                )
                            ) < 0
                        )
                            return taskList;
                        data.fromExternalTask = fromExternalTask;
                    }
                    else
                    {
                        ObjectPool.Return<BehaviorSource>(behaviorSource);
                        return -2;
                    }
                }
                if (data.overrideFields != null)
                {
                    Dictionary<string, BehaviorManager.TaskAddData.OverrideFieldValue> dictionary =
                        ObjectPool.Get<
                            Dictionary<string, BehaviorManager.TaskAddData.OverrideFieldValue>
                        >();
                    dictionary.Clear();
                    foreach (
                        KeyValuePair<
                            string,
                            BehaviorManager.TaskAddData.OverrideFieldValue
                        > overrideField in data.overrideFields
                    )
                    {
                        if (overrideField.Value.Depth != data.depth)
                            dictionary.Add(overrideField.Key, overrideField.Value);
                    }
                    ObjectPool.Return<
                        Dictionary<string, BehaviorManager.TaskAddData.OverrideFieldValue>
                    >(data.overrideFields);
                    data.overrideFields = dictionary;
                }
                --data.depth;
            }
            else
            {
                if (behaviorTree.taskList.Count == 0 && task.Disabled)
                {
                    return -6;
                }
                task.ReferenceID = behaviorTree.taskList.Count;
                behaviorTree.taskList.Add(task);
                if (data.overrideFields != null)
                    this.OverrideFields(behaviorTree, data, (object)task);
                if (data.fromExternalTask)
                {
                    if (data.parentTask == null)
                    {
                        task.NodeData.Offset = behaviorTree
                            .behavior
                            .GetBehaviorSource()
                            .RootTask
                            .NodeData
                            .Offset;
                    }
                    else
                    {
                        int index = behaviorTree.relativeChildIndex[
                            behaviorTree.relativeChildIndex.Count - 1
                        ];
                        data.parentTask.ReplaceAddChild(task, index);
                        if (!data.offset.Equals(float2.zero))
                        {
                            task.NodeData.Offset = data.offset;
                            data.offset = float2.zero;
                        }
                    }
                }
                if (task is ParentTask)
                {
                    ParentTask parentTask = task as ParentTask;
                    if (parentTask.Children == null || parentTask.Children.Count == 0)
                    {
                        data.errorTask = behaviorTree.taskList.Count - 1;
                        data.errorTaskName = string.IsNullOrEmpty(
                            behaviorTree.taskList[data.errorTask].FriendlyName
                        )
                            ? behaviorTree.taskList[data.errorTask].GetType().ToString()
                            : behaviorTree.taskList[data.errorTask].FriendlyName;
                        return -1;
                    }
                    int index4 = behaviorTree.taskList.Count - 1;
                    List<int> intList1 = ObjectPool.Get<List<int>>();
                    intList1.Clear();
                    behaviorTree.childrenIndex.Add(intList1);
                    List<int> intList2 = ObjectPool.Get<List<int>>();
                    intList2.Clear();
                    behaviorTree.childConditionalIndex.Add(intList2);
                    int count = parentTask.Children.Count;
                    for (int index5 = 0; index5 < count; ++index5)
                    {
                        behaviorTree.parentIndex.Add(index4);
                        behaviorTree.relativeChildIndex.Add(index5);
                        behaviorTree.childrenIndex[index4].Add(behaviorTree.taskList.Count);
                        data.parentTask = task as ParentTask;
                        data.parentIndex = index4;
                        if (task is Composite)
                            data.compositeParentIndex = index4;
                        behaviorTree.parentCompositeIndex.Add(data.compositeParentIndex);
                        int taskList;
                        if (
                            (
                                taskList = this.AddToTaskList(
                                    behaviorTree,
                                    parentTask.Children[index5],
                                    ref hasExternalBehavior,
                                    data
                                )
                            ) < 0
                        )
                        {
                            if (taskList == -3)
                            {
                                data.errorTask = index4;
                                data.errorTaskName = string.IsNullOrEmpty(
                                    behaviorTree.taskList[data.errorTask].FriendlyName
                                )
                                    ? behaviorTree.taskList[data.errorTask].GetType().ToString()
                                    : behaviorTree.taskList[data.errorTask].FriendlyName;
                            }
                            return taskList;
                        }
                    }
                }
                else
                {
                    behaviorTree.childrenIndex.Add((List<int>)null);
                    behaviorTree.childConditionalIndex.Add((List<int>)null);
                    if (task is Conditional)
                    {
                        int index6 = behaviorTree.taskList.Count - 1;
                        int index7 = behaviorTree.parentCompositeIndex[index6];
                        if (index7 != -1)
                            behaviorTree.childConditionalIndex[index7].Add(index6);
                    }
                }
            }
            return 0;
        }
#endif

        private void OverrideFields(
            BehaviorManager.BehaviorTree behaviorTree,
            BehaviorManager.TaskAddData data,
            object obj
        )
        {
            if (obj == null || object.Equals(obj, (object)null))
            {
                return;
            }

            FieldInfo[] serializableFields = TaskUtility.GetSerializableFields(obj.GetType());
            for (int index1 = 0; index1 < serializableFields.Length; ++index1)
            {
                object obj1 = serializableFields[index1].GetValue(obj);
                if (obj1 != null)
                {
                    if (
                        typeof(SharedVariable).IsAssignableFrom(serializableFields[index1].FieldType)
                    )
                    {
                        SharedVariable sharedVariable = this.OverrideSharedVariable(behaviorTree,
                            data,
                            serializableFields[index1].FieldType,
                            obj1 as SharedVariable);
                        if (sharedVariable != null)
                            serializableFields[index1].SetValue(obj, (object)sharedVariable);
                    }
                    else if (typeof(IList).IsAssignableFrom(serializableFields[index1].FieldType))
                    {
                        System.Type fieldType;
                        if (
                            (
                                typeof(SharedVariable).IsAssignableFrom(fieldType = serializableFields[index1]
                                    .FieldType
                                    .GetElementType())
                                || serializableFields[index1].FieldType.IsGenericType
                                && typeof(SharedVariable).IsAssignableFrom(fieldType = serializableFields[index1]
                                    .FieldType
                                    .GetGenericArguments()[0])
                            ) && obj1 is IList list
                        )
                        {
                            for (int index2 = 0; index2 < list.Count; ++index2)
                            {
                                SharedVariable sharedVariable = this.OverrideSharedVariable(behaviorTree,
                                    data,
                                    fieldType,
                                    list[index2] as SharedVariable);
                                if (sharedVariable != null)
                                    list[index2] = (object)sharedVariable;
                            }
                        }
                    }
                    else if (
                        serializableFields[index1].FieldType.IsClass
                        && !serializableFields[index1].FieldType.Equals(typeof(System.Type))
                        && !typeof(Delegate).IsAssignableFrom(serializableFields[index1].FieldType)
                        && !data.overiddenFields.Contains(obj1)
                    )
                    {
                        data.overiddenFields.Add(obj1);
                        this.OverrideFields(behaviorTree, data, obj1);
                        data.overiddenFields.Remove(obj1);
                    }

                    if (
                        TaskUtility.HasAttribute(serializableFields[index1],
                            typeof(InspectTaskAttribute))
                    )
                    {
                        if (typeof(IList).IsAssignableFrom(serializableFields[index1].FieldType))
                        {
                            if (
                                typeof(Task).IsAssignableFrom(serializableFields[index1].FieldType.GetElementType()) && obj1 is IList list1
                            )
                            {
                                for (int index3 = 0; index3 < list1.Count; ++index3)
                                    this.OverrideFields(behaviorTree,
                                        data,
                                        (object)(list1[index3] as Task));
                            }
                        }
                        else if (
                            typeof(Task).IsAssignableFrom(serializableFields[index1].FieldType)
                        )
                        {
                            this.OverrideFields(behaviorTree, data, obj1);
                        }
                    }
                }
            }
        }

        private SharedVariable OverrideSharedVariable(
            BehaviorManager.BehaviorTree behaviorTree,
            BehaviorManager.TaskAddData data,
            System.Type fieldType,
            SharedVariable sharedVariable
        )
        {
            SharedVariable sharedVariable1 = sharedVariable;
            if (sharedVariable is SharedGenericVariable)
            {
                sharedVariable = (
                    (sharedVariable as SharedGenericVariable).GetValue() as GenericVariable
                ).value;
            }
            else if (sharedVariable is SharedNamedVariable)
            {
                sharedVariable = (
                    (sharedVariable as SharedNamedVariable).GetValue() as NamedVariable
                ).value;
            }

            if (sharedVariable == null)
            {
                return (SharedVariable)null;
            }

            BehaviorManager.TaskAddData.OverrideFieldValue overrideFieldValue;
            if (
                !string.IsNullOrEmpty(sharedVariable.Name)
                && data.overrideFields.TryGetValue(sharedVariable.Name, out overrideFieldValue)
            )
            {
                SharedVariable sharedVariable2 = (SharedVariable)null;
                if (overrideFieldValue.Value is SharedVariable)
                {
                    sharedVariable2 = overrideFieldValue.Value as SharedVariable;
                }
                else if (overrideFieldValue.Value is NamedVariable)
                {
                    sharedVariable2 = (overrideFieldValue.Value as NamedVariable).value;
                    if (sharedVariable2.IsGlobal)
                    {
                        sharedVariable2 = GlobalVariables
                            .Instance
                            .GetVariable(sharedVariable2.Name);
                    }
                    else if (sharedVariable2.IsShared)
                    {
                        sharedVariable2 = behaviorTree.behavior.GetVariable(sharedVariable2.Name);
                    }
                }
                else if (overrideFieldValue.Value is GenericVariable)
                {
                    sharedVariable2 = (overrideFieldValue.Value as GenericVariable).value;
                    if (sharedVariable2.IsGlobal)
                    {
                        sharedVariable2 = GlobalVariables
                            .Instance
                            .GetVariable(sharedVariable2.Name);
                    }
                    else if (sharedVariable2.IsShared)
                    {
                        sharedVariable2 = behaviorTree.behavior.GetVariable(sharedVariable2.Name);
                    }
                }

                if (
                    sharedVariable1 is SharedNamedVariable
                    || sharedVariable1 is SharedGenericVariable
                )
                {
                    if (
                        fieldType.Equals(typeof(SharedVariable))
                        || sharedVariable2.GetType().Equals(sharedVariable.GetType())
                    )
                    {
                        switch (sharedVariable1)
                        {
                            case SharedNamedVariable _:
                                (sharedVariable1 as SharedNamedVariable).Value.value =
                                    sharedVariable2;
                                break;
                            case SharedGenericVariable _:
                                (sharedVariable1 as SharedGenericVariable).Value.value =
                                    sharedVariable2;
                                break;
                        }

                        behaviorTree
                            .behavior
                            .SetVariableValue(sharedVariable.Name, sharedVariable2.GetValue());
                    }
                }
                else if (sharedVariable2 != null)
                {
                    return sharedVariable2;
                }
            }

            return (SharedVariable)null;
        }

        public void DisableBehavior(Behavior behavior)
        {
            this.DisableBehavior(behavior, false);
        }

        public void DisableBehavior(Behavior behavior, bool paused)
        {
            this.DisableBehavior(behavior, paused, TaskStatus.Success);
        }

#if !UNITY_PLATFORM
        public async void DisableBehavior(Behavior behavior, bool paused, TaskStatus executionStatus)
        {
            if (!this.IsBehaviorEnabled(behavior))
            {
                if (this.pausedBehaviorTrees.ContainsKey(behavior) && !paused)
                {
                    await this.EnableBehavior(behavior);
                }
                else
                {
                    if (this.behaviorLoaders.Count <= 0)
                    {
                        return;
                    }

                    for (int i = this.behaviorLoaders.Count - 1; i >= 0; i--)
                    {
                        this.behaviorLoaders[i].CancelTask(behavior);
                    }
                    return;
                }
            }

            if (behavior.LogTaskChanges)
                Debug.Log(
                    $"{(object)this.RoundedTime()}: {(!paused ? (object)"Disabling" : (object)"Pausing")} {(object)((object)behavior).ToString()}"
                );
            if (paused)
            {
                if (!this.behaviorTreeMap.TryGetValue(behavior, out var behaviorTree) || this.pausedBehaviorTrees.ContainsKey(behavior))
                {
                    return;
                }

                this.pausedBehaviorTrees.Add(behavior, behaviorTree);
                behavior.ExecutionStatus = TaskStatus.Inactive;
                for (int index = 0; index < behaviorTree.taskList.Count; ++index)
                {
                    behaviorTree.taskList[index].OnPause(true);
                }

                this.behaviorTrees.Remove(behaviorTree);
            }
            else
            {
                this.DestroyBehavior(behavior, executionStatus);
            }
        }
#endif

        public void DestroyBehavior(Behavior behavior)
        {
            this.DestroyBehavior(behavior, TaskStatus.Success);
        }

        public void DestroyBehavior(Behavior behavior, TaskStatus executionStatus)
        {
            if (!this.behaviorTreeMap.TryGetValue(behavior, out var behaviorTree) || behaviorTree.destroyBehavior)
            {
                return;
            }

            behaviorTree.destroyBehavior = true;
            if (this.pausedBehaviorTrees.ContainsKey(behavior))
            {
                this.pausedBehaviorTrees.Remove(behavior);
                for (int index = 0; index < behaviorTree.taskList.Count; ++index)
                {
                    behaviorTree.taskList[index].OnPause(false);
                }

                behavior.ExecutionStatus = TaskStatus.Running;
            }

            TaskStatus status = executionStatus;
            for (int index = behaviorTree.activeStack.Count - 1; index > -1; --index)
            {
                while (behaviorTree.activeStack[index].Count > 0)
                {
                    int count = behaviorTree.activeStack[index].Count;
                    this.PopTask(behaviorTree,
                        behaviorTree.activeStack[index].Peek(),
                        index,
                        ref status,
                        true,
                        false);
                    if (count == 1)
                    {
                        break;
                    }
                }
            }

            this.RemoveChildConditionalReevaluate(behaviorTree, -1);
            for (int index = 0; index < behaviorTree.taskList.Count; ++index)
            {
                behaviorTree.taskList[index].OnBehaviorComplete();
            }

            this.behaviorTreeMap.Remove(behavior);
            this.behaviorTrees.Remove(behaviorTree);
            behaviorTree.destroyBehavior = false;
            ObjectPool.Return<BehaviorManager.BehaviorTree>(behaviorTree);
            behavior.ExecutionStatus = status;
            behavior.OnBehaviorEnded();
        }

        public void RestartBehavior(Behavior behavior)
        {
            if (!this.IsBehaviorEnabled(behavior))
            {
                return;
            }

            BehaviorManager.BehaviorTree behaviorTree = this.behaviorTreeMap[behavior];
            TaskStatus status = TaskStatus.Success;
            for (int index = behaviorTree.activeStack.Count - 1; index > -1; --index)
            {
                while (behaviorTree.activeStack[index].Count > 0)
                {
                    int count = behaviorTree.activeStack[index].Count;
                    this.PopTask(behaviorTree,
                        behaviorTree.activeStack[index].Peek(),
                        index,
                        ref status,
                        true,
                        false);
                    if (count == 1)
                    {
                        break;
                    }
                }
            }

            this.Restart(behaviorTree);
        }

        public bool IsBehaviorEnabled(Behavior behavior) =>
            this.behaviorTreeMap != null
            && this.behaviorTreeMap.Count > 0
            && behavior != null
            && behavior.ExecutionStatus == TaskStatus.Running;

        public void Update()
        {
#if !UNITY_PLATFORM
            if (!this.enabled)
            {
                return;
            }
#endif
            this.Tick();
#if !UNITY_PLATFORM
            this.LateUpdate();
#endif
        }

        public void LateUpdate()
        {
#if !UNITY_PLATFORM
            if (!this.enabled)
            {
                return;
            }
#endif
            for (int index1 = 0; index1 < this.behaviorTrees.Count; ++index1)
            {
                if (this.behaviorTrees[index1].behavior.HasEvent[9])
                {
                    for (
                        int index2 = this.behaviorTrees[index1].activeStack.Count - 1;
                        index2 > -1;
                        --index2
                    )
                    {
                        int index3 = this.behaviorTrees[index1].activeStack[index2].Peek();
                        this.behaviorTrees[index1].taskList[index3].OnLateUpdate();
                    }
                }
            }
        }

        // TODO 找个地方调用一下
        public void FixedUpdate()
        {
#if !UNITY_PLATFORM
            if (!this.enabled)
            {
                return;
            }
#endif
            for (int index = 0; index < this.behaviorTrees.Count; ++index)
            {
                if (this.behaviorTrees[index].behavior.HasEvent[10])
                {
                    this.FixedTick(this.behaviorTrees[index]);
                }
            }
        }

        private void FixedTick(BehaviorManager.BehaviorTree behaviorTree)
        {
            for (int index1 = behaviorTree.activeStack.Count - 1; index1 > -1; --index1)
            {
                int index2 = behaviorTree.activeStack[index1].Peek();
                behaviorTree.taskList[index2].OnFixedUpdate();
            }
        }

        public void Tick()
        {
            for (int index = 0; index < this.behaviorTrees.Count; ++index)
            {
                this.Tick(this.behaviorTrees[index]);
            }
        }

        public void Tick(Behavior behavior)
        {
            if (behavior == null || !this.IsBehaviorEnabled(behavior))
            {
                return;
            }

            this.Tick(this.behaviorTreeMap[behavior]);
        }

        public void FixedTick(Behavior behavior)
        {
            if (behavior == null || !this.IsBehaviorEnabled(behavior))
            {
                return;
            }

            this.FixedTick(this.behaviorTreeMap[behavior]);
        }

        private void Tick(BehaviorManager.BehaviorTree behaviorTree)
        {
            behaviorTree.executionCount = 0;
            this.ReevaluateParentTasks(behaviorTree);
            this.ReevaluateConditionalTasks(behaviorTree);
            int count1 = behaviorTree.activeStack.Count;
            for (int index = 0; index < count1; ++index)
            {
                TaskStatus status = behaviorTree.interruptionTaskStatus[index];
                int num1;
                if (index < behaviorTree.interruptionIndex.Count && (num1 = behaviorTree.interruptionIndex[index]) != -1)
                {
                    behaviorTree.interruptionIndex[index] = -1;
                    while (behaviorTree.activeStack[index].Peek() != num1)
                    {
                        int count2 = behaviorTree.activeStack[index].Count;
                        this.PopTask(behaviorTree,
                            behaviorTree.activeStack[index].Peek(),
                            index,
                            ref status,
                            true);
                        if (count2 == 1)
                        {
                            break;
                        }
                    }

                    if (
                        index < behaviorTree.activeStack.Count
                        && behaviorTree.activeStack[index].Count > 0
                        && behaviorTree.taskList[num1]
                        == behaviorTree.taskList[behaviorTree.activeStack[index].Peek()]
                    )
                    {
                        if (behaviorTree.taskList[num1] is ParentTask)
                        {
                            status = (behaviorTree.taskList[num1] as ParentTask).OverrideStatus();
                        }

                        this.PopTask(behaviorTree, num1, index, ref status, true);
                    }
                }

                int num2 = -1;
                int taskIndex;
                for (
                    ;
                    status != TaskStatus.Running
                    && index < behaviorTree.activeStack.Count
                    && behaviorTree.activeStack[index].Count > 0;
                    status = this.RunTask(behaviorTree, taskIndex, index, status)
                )
                {
                    taskIndex = behaviorTree.activeStack[index].Peek();
                    if (
                        (
                            index >= behaviorTree.activeStack.Count
                            || behaviorTree.activeStack[index].Count <= 0
                            || num2 != behaviorTree.activeStack[index].Peek()
                        ) && this.IsBehaviorEnabled(behaviorTree.behavior)
                    )
                    {
                        num2 = taskIndex;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        private void ReevaluateConditionalTasks(BehaviorManager.BehaviorTree behaviorTree)
        {
            for (int index1 = 0; index1 < behaviorTree.conditionalReevaluate.Count; ++index1)
            {
                if (behaviorTree.conditionalReevaluate[index1].compositeIndex != -1)
                {
                    int index2 = behaviorTree.conditionalReevaluate[index1].index;
                    TaskStatus taskStatus = behaviorTree.taskList[index2].OnUpdate();
                    if (taskStatus != behaviorTree.conditionalReevaluate[index1].taskStatus)
                    {
                        if (behaviorTree.behavior.LogTaskChanges)
                        {
                            int index3 = behaviorTree.parentCompositeIndex[index2];
                            Debug.Log(
                                $"{(object)this.RoundedTime()}: {(object)((object)behaviorTree.behavior).ToString()}: Conditional abort with task {(object)behaviorTree.taskList[index3].FriendlyName} ({(object)behaviorTree.taskList[index3].GetType()}, index {(object)index3}) because of conditional task {(object)behaviorTree.taskList[index2].FriendlyName} ({(object)behaviorTree.taskList[index2].GetType()}, index {(object)index2}) with status {(object)taskStatus}");
                        }

                        int compositeIndex = behaviorTree
                            .conditionalReevaluate[index1]
                            .compositeIndex;
                        for (int index4 = behaviorTree.activeStack.Count - 1; index4 > -1; --index4)
                        {
                            if (behaviorTree.activeStack[index4].Count > 0)
                            {
                                int num = behaviorTree.activeStack[index4].Peek();
                                int lca = this.FindLCA(behaviorTree, index2, num);
                                if (this.IsChild(behaviorTree, lca, compositeIndex))
                                {
                                    for (
                                        int count = behaviorTree.activeStack.Count;
                                        num != -1
                                        && num != lca
                                        && behaviorTree.activeStack.Count == count;
                                        num = behaviorTree.parentIndex[num]
                                    )
                                    {
                                        TaskStatus status = TaskStatus.Failure;
                                        behaviorTree.taskList[num].OnConditionalAbort();
                                        this.PopTask(behaviorTree, num, index4, ref status, false);
                                    }
                                }
                            }
                        }

                        for (
                            int index5 = behaviorTree.conditionalReevaluate.Count - 1;
                            index5 > index1 - 1;
                            --index5
                        )
                        {
                            BehaviorManager.BehaviorTree.ConditionalReevaluate conditionalReevaluate =
                                behaviorTree.conditionalReevaluate[index5];
                            if (
                                this.FindLCA(behaviorTree,
                                    compositeIndex,
                                    conditionalReevaluate.index) == compositeIndex
                            )
                            {
                                behaviorTree
                                    .taskList[behaviorTree.conditionalReevaluate[index5].index]
                                    .NodeData
                                    .IsReevaluating = false;
                                ObjectPool.Return<BehaviorManager.BehaviorTree.ConditionalReevaluate>(behaviorTree.conditionalReevaluate[index5]);
                                behaviorTree
                                    .conditionalReevaluateMap
                                    .Remove(behaviorTree.conditionalReevaluate[index5].index);
                                behaviorTree.conditionalReevaluate.RemoveAt(index5);
                            }
                        }

                        Composite task1 =
                            behaviorTree.taskList[behaviorTree.parentCompositeIndex[index2]]
                                as Composite;
                        for (int index6 = index1 - 1; index6 > -1; --index6)
                        {
                            BehaviorManager.BehaviorTree.ConditionalReevaluate conditionalReevaluate =
                                behaviorTree.conditionalReevaluate[index6];
                            if (
                                task1.AbortType == AbortType.LowerPriority
                                && behaviorTree.parentCompositeIndex[conditionalReevaluate.index]
                                == behaviorTree.parentCompositeIndex[index2]
                            )
                            {
                                behaviorTree
                                    .taskList[behaviorTree.conditionalReevaluate[index6].index]
                                    .NodeData
                                    .IsReevaluating = false;
                                behaviorTree.conditionalReevaluate[index6].compositeIndex = -1;
                            }
                            else if (
                                behaviorTree.parentCompositeIndex[conditionalReevaluate.index]
                                == behaviorTree.parentCompositeIndex[index2]
                            )
                            {
                                for (
                                    int index7 = 0;
                                    index7 < behaviorTree.childrenIndex[compositeIndex].Count;
                                    ++index7
                                )
                                {
                                    if (
                                        this.IsParentTask(behaviorTree,
                                            behaviorTree.childrenIndex[compositeIndex][index7],
                                            conditionalReevaluate.index)
                                    )
                                    {
                                        int index8 = behaviorTree.childrenIndex[compositeIndex][
                                            index7
                                        ];
                                        while (
                                            !(behaviorTree.taskList[index8] is Composite)
                                            && behaviorTree.childrenIndex[index8] != null
                                        )
                                            index8 = behaviorTree.childrenIndex[index8][0];
                                        if (behaviorTree.taskList[index8] is Composite)
                                        {
                                            conditionalReevaluate.compositeIndex = index8;
                                            break;
                                        }

                                        break;
                                    }
                                }
                            }
                        }

                        this.conditionalParentIndexes.Clear();
                        for (
                            int index9 = behaviorTree.parentIndex[index2];
                            index9 != compositeIndex;
                            index9 = behaviorTree.parentIndex[index9]
                        )
                            this.conditionalParentIndexes.Add(index9);
                        if (this.conditionalParentIndexes.Count == 0)
                            this.conditionalParentIndexes.Add(behaviorTree.parentIndex[index2]);
                        (behaviorTree.taskList[compositeIndex] as ParentTask).OnConditionalAbort(behaviorTree.relativeChildIndex[
                            this.conditionalParentIndexes[
                                this.conditionalParentIndexes.Count - 1
                            ]
                        ]);
                        for (
                            int index10 = this.conditionalParentIndexes.Count - 1;
                            index10 > -1;
                            --index10
                        )
                        {
                            ParentTask task2 =
                                behaviorTree.taskList[this.conditionalParentIndexes[index10]]
                                    as ParentTask;
                            if (index10 == 0)
                            {
                                task2.OnConditionalAbort(behaviorTree.relativeChildIndex[index2]);
                            }
                            else
                            {
                                task2.OnConditionalAbort(behaviorTree.relativeChildIndex[
                                    this.conditionalParentIndexes[index10 - 1]
                                ]);
                            }
                        }
#if UNITY_PLATFORM
                        behaviorTree.taskList[index2].NodeData.InterruptTime = UnityEngine.Time.realtimeSinceStartup;
#endif
                    }
                }
            }
        }

        private void ReevaluateParentTasks(BehaviorManager.BehaviorTree behaviorTree)
        {
            for (int index = behaviorTree.parentReevaluate.Count - 1; index > -1; --index)
            {
                int num = behaviorTree.parentReevaluate[index];
                if (behaviorTree.taskList[num] is Decorator)
                {
                    if (behaviorTree.taskList[num].OnUpdate() == TaskStatus.Failure)
                    {
                        this.Interrupt(behaviorTree.behavior,
                            behaviorTree.taskList[num],
                            TaskStatus.Inactive);
                    }
                }
                else if (behaviorTree.taskList[num] is Composite)
                {
                    Composite task = behaviorTree.taskList[num] as Composite;
                    if (task.OnReevaluationStarted())
                    {
                        int stackIndex = 0;
                        TaskStatus status = this.RunParentTask(behaviorTree,
                            num,
                            ref stackIndex,
                            TaskStatus.Inactive);
                        task.OnReevaluationEnded(status);
                    }
                }
            }
        }

        private TaskStatus RunTask(
            BehaviorManager.BehaviorTree behaviorTree,
            int taskIndex,
            int stackIndex,
            TaskStatus previousStatus
        )
        {
            Task task1 = behaviorTree.taskList[taskIndex];
            if (task1 == null)
            {
                return previousStatus;
            }

            if (task1.Disabled)
            {
                if (behaviorTree.behavior.LogTaskChanges)
                {
                    Debug.Log(
                        $"{(object)this.RoundedTime()}: {(object)((object)behaviorTree.behavior).ToString()}: Skip task {(object)behaviorTree.taskList[taskIndex].FriendlyName} ({(object)behaviorTree.taskList[taskIndex].GetType()}, index {(object)taskIndex}) at stack index {(object)stackIndex} (task disabled)");
                }

                if (behaviorTree.parentIndex[taskIndex] != -1)
                {
                    ParentTask task2 =
                        behaviorTree.taskList[behaviorTree.parentIndex[taskIndex]] as ParentTask;
                    if (!task2.CanRunParallelChildren())
                    {
                        task2.OnChildExecuted(TaskStatus.Inactive);
                    }
                    else
                    {
                        task2.OnChildExecuted(behaviorTree.relativeChildIndex[taskIndex],
                            TaskStatus.Inactive);
                        this.RemoveStack(behaviorTree, stackIndex);
                    }
                }

                return previousStatus;
            }

            TaskStatus status1 = previousStatus;
            if (
                !task1.IsInstant
                && (
                    behaviorTree.nonInstantTaskStatus[stackIndex] == TaskStatus.Failure
                    || behaviorTree.nonInstantTaskStatus[stackIndex] == TaskStatus.Success
                )
            )
            {
                TaskStatus instantTaskStatu = behaviorTree.nonInstantTaskStatus[stackIndex];
                this.PopTask(behaviorTree, taskIndex, stackIndex, ref instantTaskStatu, true);
                return instantTaskStatu;
            }

            this.PushTask(behaviorTree, taskIndex, stackIndex);
            if (this.breakpointTree != null)
            {
                return TaskStatus.Running;
            }

            TaskStatus status2 = !(task1 is ParentTask)
                ? task1.OnUpdate()
                : (task1 as ParentTask).OverrideStatus(this.RunParentTask(behaviorTree, taskIndex, ref stackIndex, status1));
            if (status2 != TaskStatus.Running)
            {
                if (task1.IsInstant)
                {
                    this.PopTask(behaviorTree, taskIndex, stackIndex, ref status2, true);
                }
                else
                {
                    behaviorTree.nonInstantTaskStatus[stackIndex] = status2;
                }
            }

            return status2;
        }

        private TaskStatus RunParentTask(
            BehaviorManager.BehaviorTree behaviorTree,
            int taskIndex,
            ref int stackIndex,
            TaskStatus status
        )
        {
            ParentTask task = behaviorTree.taskList[taskIndex] as ParentTask;
            if (
                !task.CanRunParallelChildren()
                || task.OverrideStatus(TaskStatus.Running) != TaskStatus.Running
            )
            {
                TaskStatus taskStatus = TaskStatus.Inactive;
                int num1 = stackIndex;
                int num2 = -1;
                Behavior behavior = behaviorTree.behavior;
                while (
                    task.CanExecute()
                    && (taskStatus != TaskStatus.Running || task.CanRunParallelChildren())
                    && this.IsBehaviorEnabled(behavior)
                    && behaviorTree.childrenIndex.Count > taskIndex
                )
                {
                    List<int> intList = behaviorTree.childrenIndex[taskIndex];
                    int num3 = task.CurrentChildIndex();
                    if (num3 == -1)
                    {
                        status = taskStatus = TaskStatus.Running;
                    }
                    else
                    {
                        if (
                            this.executionsPerTick
                            == BehaviorManager.ExecutionsPerTickType.NoDuplicates
                            && num3 == num2
                            || this.executionsPerTick == BehaviorManager.ExecutionsPerTickType.Count
                            && behaviorTree.executionCount >= this.maxTaskExecutionsPerTick
                        )
                        {
                            if (
                                this.executionsPerTick
                                == BehaviorManager.ExecutionsPerTickType.Count
                            )
                            {
                                Debug.LogWarning($"{(object)this.RoundedTime()}: {(object)((object)behaviorTree.behavior).ToString()}: More than the specified number of task executions per tick ({(object)this.maxTaskExecutionsPerTick}) have executed, returning early.");
                            }

                            status = TaskStatus.Running;
                            break;
                        }

                        num2 = num3;
                        if (task.CanRunParallelChildren())
                        {
                            behaviorTree.activeStack.Add(ObjectPool.Get<Stack<int>>());
                            behaviorTree.interruptionIndex.Add(-1);
                            behaviorTree.interruptionTaskStatus.Add(TaskStatus.Failure);
                            behaviorTree.nonInstantTaskStatus.Add(TaskStatus.Inactive);
                            stackIndex = behaviorTree.activeStack.Count - 1;
                            task.OnChildStarted(num3);
                        }
                        else
                        {
                            task.OnChildStarted();
                        }

                        status = taskStatus = this.RunTask(behaviorTree,
                            intList[num3],
                            stackIndex,
                            status);
                    }
                }

                stackIndex = num1;
            }

            return status;
        }

        private void PushTask(
            BehaviorManager.BehaviorTree behaviorTree,
            int taskIndex,
            int stackIndex
        )
        {
            if (
                !this.IsBehaviorEnabled(behaviorTree.behavior)
                || stackIndex >= behaviorTree.activeStack.Count
            )
                return;
            Stack<int> active = behaviorTree.activeStack[stackIndex];
            if (active.Count != 0 && active.Peek() == taskIndex)
            {
                return;
            }

            active.Push(taskIndex);
            behaviorTree.nonInstantTaskStatus[stackIndex] = TaskStatus.Running;
            ++behaviorTree.executionCount;
            Task task = behaviorTree.taskList[taskIndex];
#if UNITY_PLATFORM
            task.NodeData.PushTime = UnityEngine.Time.realtimeSinceStartup;
#endif
            task.NodeData.ExecutionStatus = TaskStatus.Running;
            if (task.NodeData.IsBreakpoint && this.onTaskBreakpoint != null)
            {
                this.breakpointTree = behaviorTree.behavior;
                this.onTaskBreakpoint();
            }

            if (behaviorTree.behavior.LogTaskChanges)
            {
                Debug.Log($"{(object)this.RoundedTime()}: {(object)((object)behaviorTree.behavior).ToString()}: Push task {(object)task.FriendlyName} ({(object)task.GetType()}, index {(object)taskIndex}) at stack index {(object)stackIndex}");
            }

            task.OnStart();
            if (!(task is ParentTask) || !(task as ParentTask).CanReevaluate())
            {
                return;
            }

            behaviorTree.parentReevaluate.Add(taskIndex);
        }

        private void PopTask(
            BehaviorManager.BehaviorTree behaviorTree,
            int taskIndex,
            int stackIndex,
            ref TaskStatus status,
            bool popChildren
        )
        {
            this.PopTask(behaviorTree, taskIndex, stackIndex, ref status, popChildren, true);
        }

        private void PopTask(
            BehaviorManager.BehaviorTree behaviorTree,
            int taskIndex,
            int stackIndex,
            ref TaskStatus status,
            bool popChildren,
            bool notifyOnEmptyStack
        )
        {
            if (
                !this.IsBehaviorEnabled(behaviorTree.behavior)
                || stackIndex >= behaviorTree.activeStack.Count
                || behaviorTree.activeStack[stackIndex].Count == 0
                || taskIndex != behaviorTree.activeStack[stackIndex].Peek()
            )
            {
                return;
            }

            behaviorTree.activeStack[stackIndex].Pop();
            behaviorTree.nonInstantTaskStatus[stackIndex] = TaskStatus.Inactive;
            this.StopThirdPartyTask(behaviorTree, taskIndex);
            Task task1 = behaviorTree.taskList[taskIndex];
            task1.OnEnd();
            int index1 = behaviorTree.parentIndex[taskIndex];
            task1.NodeData.PushTime = -1f;
#if UNITY_PLATFORM
            task1.NodeData.PopTime = UnityEngine.Time.realtimeSinceStartup;
#endif
            task1.NodeData.ExecutionStatus = status;
            if (behaviorTree.behavior.LogTaskChanges)
            {
                Debug.Log($"{(object)this.RoundedTime()}: {(object)((object)behaviorTree.behavior).ToString()}: Pop task {(object)task1.FriendlyName} ({(object)task1.GetType()}, index {(object)taskIndex}) at stack index {(object)stackIndex} with status {(object)status}");
            }

            if (index1 != -1)
            {
                if (task1 is Conditional)
                {
                    int index2 = behaviorTree.parentCompositeIndex[taskIndex];
                    if (index2 != -1)
                    {
                        Composite task2 = behaviorTree.taskList[index2] as Composite;
                        if (task2.AbortType != AbortType.None)
                        {
                            BehaviorManager.BehaviorTree.ConditionalReevaluate conditionalReevaluate1;
                            if (
                                behaviorTree
                                .conditionalReevaluateMap
                                .TryGetValue(taskIndex, out conditionalReevaluate1)
                            )
                            {
                                conditionalReevaluate1.compositeIndex =
                                    task2.AbortType == AbortType.LowerPriority ? -1 : index2;
                                conditionalReevaluate1.taskStatus = status;
                                task1.NodeData.IsReevaluating =
                                    task2.AbortType != AbortType.LowerPriority;
                            }
                            else
                            {
                                BehaviorManager.BehaviorTree.ConditionalReevaluate conditionalReevaluate2 =
                                    ObjectPool.Get<BehaviorManager.BehaviorTree.ConditionalReevaluate>();
                                conditionalReevaluate2.Initialize(taskIndex,
                                    status,
                                    stackIndex,
                                    task2.AbortType == AbortType.LowerPriority ? -1 : index2);
                                behaviorTree.conditionalReevaluate.Add(conditionalReevaluate2);
                                behaviorTree
                                    .conditionalReevaluateMap
                                    .Add(taskIndex, conditionalReevaluate2);
                                task1.NodeData.IsReevaluating =
                                    task2.AbortType == AbortType.Self
                                    || task2.AbortType == AbortType.Both;
                            }
                        }
                    }
                }

                ParentTask task3 = behaviorTree.taskList[index1] as ParentTask;
                if (!task3.CanRunParallelChildren())
                {
                    task3.OnChildExecuted(status);
                    status = task3.Decorate(status);
                }
                else
                {
                    task3.OnChildExecuted(behaviorTree.relativeChildIndex[taskIndex], status);
                }
            }

            if (task1 is ParentTask)
            {
                ParentTask parentTask = task1 as ParentTask;
                if (parentTask.CanReevaluate())
                {
                    for (
                        int index3 = behaviorTree.parentReevaluate.Count - 1;
                        index3 > -1;
                        --index3
                    )
                    {
                        if (behaviorTree.parentReevaluate[index3] == taskIndex)
                        {
                            behaviorTree.parentReevaluate.RemoveAt(index3);
                            break;
                        }
                    }
                }

                if (parentTask is Composite)
                {
                    Composite composite = parentTask as Composite;
                    if (
                        composite.AbortType == AbortType.Self
                        || composite.AbortType == AbortType.None
                        || behaviorTree.activeStack[stackIndex].Count == 0
                    )
                    {
                        this.RemoveChildConditionalReevaluate(behaviorTree, taskIndex);
                    }
                    else if (
                        composite.AbortType == AbortType.LowerPriority
                        || composite.AbortType == AbortType.Both
                    )
                    {
                        int num1 = behaviorTree.parentCompositeIndex[taskIndex];
                        if (num1 != -1)
                        {
                            if (
                                !(
                                    behaviorTree.taskList[num1] as ParentTask
                                ).CanRunParallelChildren()
                            )
                            {
                                for (
                                    int index4 = 0;
                                    index4 < behaviorTree.childConditionalIndex[taskIndex].Count;
                                    ++index4
                                )
                                {
                                    int num2 = behaviorTree.childConditionalIndex[taskIndex][
                                        index4
                                    ];
                                    BehaviorManager.BehaviorTree.ConditionalReevaluate conditionalReevaluate3;
                                    if (
                                        behaviorTree
                                        .conditionalReevaluateMap
                                        .TryGetValue(num2, out conditionalReevaluate3)
                                    )
                                    {
                                        if (
                                            !(
                                                behaviorTree.taskList[num1] as ParentTask
                                            ).CanRunParallelChildren()
                                        )
                                        {
                                            conditionalReevaluate3.compositeIndex =
                                                behaviorTree.parentCompositeIndex[taskIndex];
                                            behaviorTree.taskList[num2].NodeData.IsReevaluating =
                                                true;
                                        }
                                        else
                                        {
                                            for (
                                                int index5 =
                                                    behaviorTree.conditionalReevaluate.Count - 1;
                                                index5 > index4 - 1;
                                                --index5
                                            )
                                            {
                                                BehaviorManager.BehaviorTree.ConditionalReevaluate conditionalReevaluate4 =
                                                    behaviorTree.conditionalReevaluate[index5];
                                                if (
                                                    this.FindLCA(behaviorTree,
                                                        num1,
                                                        conditionalReevaluate4.index) == num1
                                                )
                                                {
                                                    behaviorTree
                                                        .taskList[
                                                            behaviorTree
                                                                .conditionalReevaluate[index5]
                                                                .index
                                                        ]
                                                        .NodeData
                                                        .IsReevaluating = false;
                                                    ObjectPool.Return<BehaviorManager.BehaviorTree.ConditionalReevaluate>(behaviorTree.conditionalReevaluate[index5]);
                                                    behaviorTree
                                                        .conditionalReevaluateMap
                                                        .Remove(behaviorTree
                                                            .conditionalReevaluate[index5]
                                                            .index);
                                                    behaviorTree
                                                        .conditionalReevaluate
                                                        .RemoveAt(index5);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                this.RemoveChildConditionalReevaluate(behaviorTree, taskIndex);
                            }
                        }

                        for (
                            int index6 = 0;
                            index6 < behaviorTree.conditionalReevaluate.Count;
                            ++index6
                        )
                        {
                            if (
                                behaviorTree.conditionalReevaluate[index6].compositeIndex
                                == taskIndex
                            )
                            {
                                behaviorTree.conditionalReevaluate[index6].compositeIndex =
                                    behaviorTree.parentCompositeIndex[taskIndex];
                            }
                        }
                    }
                }
            }

            if (popChildren)
            {
                for (int index7 = behaviorTree.activeStack.Count - 1; index7 > stackIndex; --index7)
                {
                    if (
                        behaviorTree.activeStack[index7].Count > 0
                        && this.IsParentTask(behaviorTree,
                            taskIndex,
                            behaviorTree.activeStack[index7].Peek())
                    )
                    {
                        TaskStatus status1 = TaskStatus.Failure;
                        for (int count = behaviorTree.activeStack[index7].Count; count > 0; --count)
                        {
                            this.PopTask(behaviorTree,
                                behaviorTree.activeStack[index7].Peek(),
                                index7,
                                ref status1,
                                false,
                                notifyOnEmptyStack);
                        }
                    }
                }
            }

            if (
                stackIndex >= behaviorTree.activeStack.Count
                || behaviorTree.activeStack[stackIndex].Count != 0
            )
            {
                return;
            }

            if (stackIndex == 0)
            {
                if (notifyOnEmptyStack)
                {
                    if (behaviorTree.behavior.RestartWhenComplete)
                    {
                        this.Restart(behaviorTree);
                    }
                    else
                    {
                        this.DisableBehavior(behaviorTree.behavior, false, status);
                    }
                }

                status = TaskStatus.Inactive;
            }
            else
            {
                this.RemoveStack(behaviorTree, stackIndex);
                status = TaskStatus.Running;
            }
        }

        private void RemoveChildConditionalReevaluate(
            BehaviorManager.BehaviorTree behaviorTree,
            int compositeIndex
        )
        {
            for (int index1 = behaviorTree.conditionalReevaluate.Count - 1; index1 > -1; --index1)
            {
                if (behaviorTree.conditionalReevaluate[index1].compositeIndex == compositeIndex)
                {
                    ObjectPool.Return<BehaviorManager.BehaviorTree.ConditionalReevaluate>(behaviorTree.conditionalReevaluate[index1]);
                    int index2 = behaviorTree.conditionalReevaluate[index1].index;
                    behaviorTree.conditionalReevaluateMap.Remove(index2);
                    behaviorTree.conditionalReevaluate.RemoveAt(index1);
                    behaviorTree.taskList[index2].NodeData.IsReevaluating = false;
                }
            }
        }

        private void Restart(BehaviorManager.BehaviorTree behaviorTree)
        {
            if (behaviorTree.behavior.LogTaskChanges)
            {
                Debug.Log($"{(object)this.RoundedTime()}: Restarting {(object)((object)behaviorTree.behavior).ToString()}");
            }

            this.RemoveChildConditionalReevaluate(behaviorTree, -1);
            if (behaviorTree.behavior.ResetValuesOnRestart)
            {
                behaviorTree.behavior.SaveResetValues();
            }

            for (int index = 0; index < behaviorTree.taskList.Count; ++index)
            {
                behaviorTree.taskList[index].OnBehaviorRestart();
            }

            behaviorTree.behavior.OnBehaviorRestarted();
            this.PushTask(behaviorTree, 0, 0);
        }

        private bool IsParentTask(
            BehaviorManager.BehaviorTree behaviorTree,
            int possibleParent,
            int possibleChild
        )
        {
            int num;
            for (int index = possibleChild; index != -1; index = num)
            {
                num = behaviorTree.parentIndex[index];
                if (num == possibleParent)
                {
                    return true;
                }
            }

            return false;
        }

        public void Interrupt(
            Behavior behavior,
            Task task,
            TaskStatus interruptTaskStatus = TaskStatus.Failure
        )
        {
            this.Interrupt(behavior, task, task, interruptTaskStatus);
        }

        public void Interrupt(
            Behavior behavior,
            Task task,
            Task interruptionTask,
            TaskStatus interruptTaskStatus = TaskStatus.Failure
        )
        {
            if (!this.IsBehaviorEnabled(behavior))
            {
                return;
            }

            int num = -1;
            BehaviorManager.BehaviorTree behaviorTree = this.behaviorTreeMap[behavior];
            for (int index = 0; index < behaviorTree.taskList.Count; ++index)
            {
                if (behaviorTree.taskList[index].ReferenceID == task.ReferenceID)
                {
                    num = index;
                    break;
                }
            }

            if (num <= -1)
            {
                return;
            }

            for (int index1 = 0; index1 < behaviorTree.activeStack.Count; ++index1)
            {
                if (behaviorTree.activeStack[index1].Count > 0)
                {
                    for (
                        int index2 = behaviorTree.activeStack[index1].Peek();
                        index2 != -1;
                        index2 = behaviorTree.parentIndex[index2]
                    )
                    {
                        if (index2 == num)
                        {
                            behaviorTree.interruptionIndex[index1] = num;
                            behaviorTree.interruptionTaskStatus[index1] = interruptTaskStatus;
                            if (behavior.LogTaskChanges)
                            {
                                Debug.Log($"{(object)this.RoundedTime()}: {(object)((object)behaviorTree.behavior).ToString()}: Interrupt task {(object)task.FriendlyName} ({(object)task.GetType().ToString()}) with index {(object)num} at stack index {(object)index1}");
                            }
#if UNITY_PLATFORM
                            interruptionTask.NodeData.InterruptTime = UnityEngine.Time.realtimeSinceStartup;
#endif
                            break;
                        }
                    }
                }
            }
        }

        public void StopThirdPartyTask(BehaviorManager.BehaviorTree behaviorTree, int taskIndex)
        {
            this.thirdPartyTaskCompare.Task = behaviorTree.taskList[taskIndex];
            if (!this.taskObjectMap.TryGetValue(this.thirdPartyTaskCompare, out var key))
            {
                return;
            }

            BehaviorManager.ThirdPartyObjectType thirdPartyObjectType = this.objectTaskMap[key].ThirdPartyObjectType;
            if (BehaviorManager.invokeParameters == null)
            {
                BehaviorManager.invokeParameters = new object[1];
            }

            BehaviorManager.invokeParameters[0] = (object)behaviorTree.taskList[taskIndex];
            switch (thirdPartyObjectType)
            {
                case BehaviorManager.ThirdPartyObjectType.PlayMaker:
                    BehaviorManager
                        .PlayMakerStopMethod
                        .Invoke((object)null, BehaviorManager.invokeParameters);
                    break;
                case BehaviorManager.ThirdPartyObjectType.uScript:
                    BehaviorManager
                        .UScriptStopMethod
                        .Invoke((object)null, BehaviorManager.invokeParameters);
                    break;
                case BehaviorManager.ThirdPartyObjectType.DialogueSystem:
                    BehaviorManager
                        .DialogueSystemStopMethod
                        .Invoke((object)null, BehaviorManager.invokeParameters);
                    break;
                case BehaviorManager.ThirdPartyObjectType.uSequencer:
                    BehaviorManager
                        .USequencerStopMethod
                        .Invoke((object)null, BehaviorManager.invokeParameters);
                    break;
            }

            this.RemoveActiveThirdPartyTask(behaviorTree.taskList[taskIndex]);
        }

        public void RemoveActiveThirdPartyTask(Task task)
        {
            this.thirdPartyTaskCompare.Task = task;
            if (!this.taskObjectMap.TryGetValue(this.thirdPartyTaskCompare, out var key))
            {
                return;
            }

            ObjectPool.Return<object>(key);
            this.taskObjectMap.Remove(this.thirdPartyTaskCompare);
            this.objectTaskMap.Remove(key);
        }

        private void RemoveStack(BehaviorManager.BehaviorTree behaviorTree, int stackIndex)
        {
            Stack<int> active = behaviorTree.activeStack[stackIndex];
            active.Clear();
            ObjectPool.Return<Stack<int>>(active);
            behaviorTree.activeStack.RemoveAt(stackIndex);
            behaviorTree.interruptionIndex.RemoveAt(stackIndex);
            behaviorTree.nonInstantTaskStatus.RemoveAt(stackIndex);
        }

        private int FindLCA(
            BehaviorManager.BehaviorTree behaviorTree,
            int taskIndex1,
            int taskIndex2
        )
        {
            HashSet<int> intSet = ObjectPool.Get<HashSet<int>>();
            intSet.Clear();
            for (int index = taskIndex1; index != -1; index = behaviorTree.parentIndex[index])
            {
                intSet.Add(index);
            }

            int index1 = taskIndex2;
            while (!intSet.Contains(index1))
            {
                index1 = behaviorTree.parentIndex[index1];
            }

            ObjectPool.Return<HashSet<int>>(intSet);
            return index1;
        }

        private bool IsChild(
            BehaviorManager.BehaviorTree behaviorTree,
            int taskIndex1,
            int taskIndex2
        )
        {
            for (int index = taskIndex1; index != -1; index = behaviorTree.parentIndex[index])
            {
                if (index == taskIndex2)
                {
                    return true;
                }
            }

            return false;
        }

        public List<Task> GetActiveTasks(Behavior behavior)
        {
            if (!this.IsBehaviorEnabled(behavior))
            {
                return (List<Task>)null;
            }

            List<Task> activeTasks = new List<Task>();
            BehaviorManager.BehaviorTree behaviorTree = this.behaviorTreeMap[behavior];
            for (int index = 0; index < behaviorTree.activeStack.Count; ++index)
            {
                Task task = behaviorTree.taskList[behaviorTree.activeStack[index].Peek()];
                if (task is BehaviorDesigner.Runtime.Tasks.Action)
                {
                    activeTasks.Add(task);
                }
            }

            return activeTasks;
        }

        public bool MapObjectToTask(
            object objectKey,
            Task task,
            BehaviorManager.ThirdPartyObjectType objectType
        )
        {
            if (this.objectTaskMap.ContainsKey(objectKey))
            {
                string str = string.Empty;
                switch (objectType)
                {
                    case BehaviorManager.ThirdPartyObjectType.PlayMaker:
                        str = "PlayMaker FSM";
                        break;
                    case BehaviorManager.ThirdPartyObjectType.uScript:
                        str = "uScript Graph";
                        break;
                    case BehaviorManager.ThirdPartyObjectType.DialogueSystem:
                        str = "Dialogue System";
                        break;
                    case BehaviorManager.ThirdPartyObjectType.uSequencer:
                        str = "uSequencer sequence";
                        break;
                }

                Debug.LogError($"Only one behavior can be mapped to the same instance of the {(object)str}.");
                return false;
            }

            BehaviorManager.ThirdPartyTask key = ObjectPool.Get<BehaviorManager.ThirdPartyTask>();
            key.Initialize(task, objectType);
            this.objectTaskMap.Add(objectKey, key);
            this.taskObjectMap.Add(key, objectKey);
            return true;
        }

        public Task TaskForObject(object objectKey)
        {
            BehaviorManager.ThirdPartyTask thirdPartyTask;
            return !this.objectTaskMap.TryGetValue(objectKey, out thirdPartyTask)
                ? (Task)null
                : thirdPartyTask.Task;
        }

        private Decimal RoundedTime()
        {
            return Math.Round((Decimal)DateTime.UtcNow.ConvertDateToTimestamp(), 5, MidpointRounding.AwayFromZero);
        }


        public List<Task> GetTaskList(Behavior behavior)
        {
            return !this.IsBehaviorEnabled(behavior)
                ? (List<Task>)null
                : this.behaviorTreeMap[behavior].taskList;
        }

        public enum ExecutionsPerTickType
        {
            NoDuplicates,
            Count,
        }

        public delegate void BehaviorManagerHandler();

        public class BehaviorTree
        {
            public List<Task> taskList = new List<Task>();

            public List<int> parentIndex = new List<int>();

            public List<List<int>> childrenIndex = new List<List<int>>();

            public List<int> relativeChildIndex = new List<int>();

            public List<Stack<int>> activeStack = new List<Stack<int>>();

            public List<TaskStatus> nonInstantTaskStatus = new List<TaskStatus>();

            public List<int> interruptionIndex = new List<int>();

            public List<TaskStatus> interruptionTaskStatus = new List<TaskStatus>();

            public List<BehaviorManager.BehaviorTree.ConditionalReevaluate> conditionalReevaluate =
                new List<BehaviorManager.BehaviorTree.ConditionalReevaluate>();

            public Dictionary<
                int,
                BehaviorManager.BehaviorTree.ConditionalReevaluate
            > conditionalReevaluateMap =
                new Dictionary<int, BehaviorManager.BehaviorTree.ConditionalReevaluate>();

            public List<int> parentReevaluate = new List<int>();

            public List<int> parentCompositeIndex = new List<int>();

            public List<List<int>> childConditionalIndex = new List<List<int>>();

            public int executionCount;

            public Behavior behavior;

            public bool destroyBehavior;

            public string errorState;

            public void Initialize(Behavior b)
            {
                this.behavior = b;
                for (int index = this.childrenIndex.Count - 1; index > -1; --index)
                {
                    ObjectPool.Return<List<int>>(this.childrenIndex[index]);
                }

                for (int index = this.activeStack.Count - 1; index > -1; --index)
                {
                    ObjectPool.Return<Stack<int>>(this.activeStack[index]);
                }

                for (int index = this.childConditionalIndex.Count - 1; index > -1; --index)
                {
                    ObjectPool.Return<List<int>>(this.childConditionalIndex[index]);
                }

                this.taskList.Clear();
                this.parentIndex.Clear();
                this.childrenIndex.Clear();
                this.relativeChildIndex.Clear();
                this.activeStack.Clear();
                this.nonInstantTaskStatus.Clear();
                this.interruptionIndex.Clear();
                this.interruptionTaskStatus.Clear();
                this.conditionalReevaluate.Clear();
                this.conditionalReevaluateMap.Clear();
                this.parentReevaluate.Clear();
                this.parentCompositeIndex.Clear();
                this.childConditionalIndex.Clear();
            }

            public class ConditionalReevaluate
            {
                public int index;
                public TaskStatus taskStatus;
                public int compositeIndex = -1;
                public int stackIndex = -1;

                public void Initialize(int i, TaskStatus status, int stack, int composite)
                {
                    this.index = i;
                    this.taskStatus = status;
                    this.stackIndex = stack;
                    this.compositeIndex = composite;
                }
            }
        }

        public enum ThirdPartyObjectType
        {
            PlayMaker,
            uScript,
            DialogueSystem,
            uSequencer,
        }

        public class ThirdPartyTask
        {
            private Task task;
            private BehaviorManager.ThirdPartyObjectType thirdPartyObjectType;

            public Task Task
            {
                get => this.task;
                set => this.task = value;
            }

            public BehaviorManager.ThirdPartyObjectType ThirdPartyObjectType =>
                this.thirdPartyObjectType;

            public void Initialize(Task t, BehaviorManager.ThirdPartyObjectType objectType)
            {
                this.task = t;
                this.thirdPartyObjectType = objectType;
            }
        }

        public class ThirdPartyTaskComparer : IEqualityComparer<BehaviorManager.ThirdPartyTask>
        {
            public bool Equals(
                BehaviorManager.ThirdPartyTask a,
                BehaviorManager.ThirdPartyTask b
            ) =>
                !object.ReferenceEquals((object)a, (object)null)
                && !object.ReferenceEquals((object)b, (object)null)
                && a.Task.Equals((object)b.Task);

            public int GetHashCode(BehaviorManager.ThirdPartyTask obj) =>
                obj != null ? obj.Task.GetHashCode() : 0;
        }

        public class TaskAddData
        {
            public bool fromExternalTask;
            public ParentTask parentTask;
            public int parentIndex = -1;
            public int depth;
            public int compositeParentIndex = -1;
            public float2 offset;

            public Dictionary<string, BehaviorManager.TaskAddData.OverrideFieldValue> overrideFields;

            public HashSet<object> overiddenFields = new HashSet<object>();
            public int errorTask = -1;
            public string errorTaskName = string.Empty;

            public void Initialize()
            {
                this.fromExternalTask = false;
                this.parentTask = (ParentTask)null;
                this.parentIndex = -1;
                this.depth = 0;
                this.compositeParentIndex = -1;
                this.overrideFields = (Dictionary<string, BehaviorManager.TaskAddData.OverrideFieldValue>)null;
            }

            public void Destroy()
            {
                if (this.overrideFields != null)
                {
                    foreach (KeyValuePair<string, BehaviorManager.TaskAddData.OverrideFieldValue> overrideField in this.overrideFields)
                    {
                        ObjectPool.Return<KeyValuePair<string, BehaviorManager.TaskAddData.OverrideFieldValue>>(overrideField);
                    }
                }

                ObjectPool.Return<Dictionary<string, BehaviorManager.TaskAddData.OverrideFieldValue>>(this.overrideFields);
            }

            public class OverrideFieldValue
            {
                private object value;
                private int depth;

                public object Value => this.value;

                public int Depth => this.depth;

                public void Initialize(object v, int d)
                {
                    this.value = v;
                    this.depth = d;
                }
            }
        }

        private class BehaviorLoaderTask
        {
            private Behavior behavior;

            private CSTask loaderTask;

            private CancellationTokenSource cts;
            public CSTask LoaderTask => this.loaderTask;

            private Action<BehaviorLoaderTask> afterLoadSuccess;

            public BehaviorLoaderTask(Behavior behavior, Func<Behavior, BehaviorTree> loader, Action<Behavior, BehaviorTree> loadSuccess, Action<BehaviorLoaderTask> afterLoadSuccess)
            {
                this.behavior = behavior;
                this.cts = new CancellationTokenSource();
                this.afterLoadSuccess = afterLoadSuccess;
                this.loaderTask = CSTask.Run(() =>
                    {
                        while (true)
                        {
                            if (this.cts.Token.IsCancellationRequested)
                            {
                                break;
                            }

                            var behaviorTree = loader(behavior);
                            loadSuccess(behavior, behaviorTree);
                            CSTask.Delay((int)1 / 60);
                            afterLoadSuccess(this);
                        }
                    },
                    this.cts.Token);
            }

            public void CancelTask(Behavior behavior)
            {
                if (this.behavior == behavior)
                {
                    this.cts.Cancel();
                    this.afterLoadSuccess(this);
                }
            }
        }
    }
}