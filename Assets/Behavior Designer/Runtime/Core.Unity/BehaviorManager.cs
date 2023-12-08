using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BehaviorDesigner.Runtime.Tasks;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Task = BehaviorDesigner.Runtime.Tasks.Task;

namespace BehaviorDesigner.Runtime
{
    [AddComponentMenu("Behavior Designer/Behavior Manager")]
    public partial class BehaviorManager : MonoBehaviour
    {
        [SerializeField] private UpdateIntervalType updateInterval;

        [SerializeField] private float updateIntervalSeconds;

        [SerializeField] private BehaviorManager.ExecutionsPerTickType executionsPerTick;

        [SerializeField] private int maxTaskExecutionsPerTick = 100;

        private WaitForSeconds updateWait;

        private UnityEngine.Object lockObject = new UnityEngine.Object();

        private List<BehaviorManager.BehaviorThreadLoader> activeThreads;

        private IEnumerator threadLoaderCoroutine;

        public void OnApplicationQuit()
        {
            for (int index = this.behaviorTrees.Count - 1; index > -1; --index)
            {
                this.DisableBehavior(this.behaviorTrees[index].behavior);
            }
        }

        public void Awake()
        {
            BehaviorManager.instance = this;
            BehaviorManager.isPlaying = true;
            this.UpdateIntervalChanged();
        }

        private void UpdateIntervalChanged()
        {
            this.StopCoroutine("CoroutineUpdate");
            if (this.updateInterval == UpdateIntervalType.EveryFrame)
            {
                this.enabled = true;
            }
            else if (this.updateInterval == UpdateIntervalType.SpecifySeconds)
            {
                if (Application.isPlaying)
                {
                    this.updateWait = new WaitForSeconds(this.updateIntervalSeconds);
                    this.StartCoroutine("CoroutineUpdate");
                }

                this.enabled = false;
            }
            else
            {
                this.enabled = false;
            }
        }

        [DebuggerHidden]
        private IEnumerator CoroutineUpdate()
        {
            yield return updateWait;
        }

        public void EnableBehavior(Behavior behavior)
        {
            if (this.IsBehaviorEnabled(behavior))
            {
                return;
            }

            BehaviorTree behaviorTree;
            if (this.pausedBehaviorTrees.TryGetValue(behavior, out behaviorTree))
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
                BehaviorManager.BehaviorThreadLoader behaviorThreadLoader =
                    new BehaviorManager.BehaviorThreadLoader(behavior,
                        new Func<
                            Behavior,
                            GameObject,
                            string,
                            Transform,
                            BehaviorManager.BehaviorTree
                        >(this.LoadBehavior));
                Thread thread = new Thread(new ThreadStart(behaviorThreadLoader.LoadBehavior));
                behaviorThreadLoader.Thread = thread;
                thread.Start();
                if (this.activeThreads == null)
                {
                    this.activeThreads = new List<BehaviorManager.BehaviorThreadLoader>();
                }

                this.activeThreads.Add(behaviorThreadLoader);
                if (this.threadLoaderCoroutine != null)
                {
                    return;
                }

                this.threadLoaderCoroutine = this.CheckThreadLoaders();
                this.StartCoroutine(this.threadLoaderCoroutine);
            }
            else
            {
                behaviorTree = this.LoadBehavior(behavior,
                    behavior.gameObject,
                    behavior.gameObject.name,
                    behavior.transform);
                this.LoadBehaviorComplete(behavior, behaviorTree);
            }
        }

        public void DisableBehavior(Behavior behavior, bool paused, TaskStatus executionStatus)
        {
            if (!this.IsBehaviorEnabled(behavior))
            {
                if (this.pausedBehaviorTrees.ContainsKey(behavior) && !paused)
                {
                    this.EnableBehavior(behavior);
                }
                else
                {
                    if (this.activeThreads == null || this.activeThreads.Count <= 0)
                    {
                        return;
                    }

                    for (int index = 0; index < this.activeThreads.Count; ++index)
                    {
                        if (this.activeThreads[index].Behavior == behavior)
                        {
                            this.activeThreads[index].Thread.Abort();
                            this.activeThreads.RemoveAt(index);
                            break;
                        }
                    }

                    return;
                }
            }

            if (behavior.LogTaskChanges)
                Debug.Log($"{(object)this.RoundedTime()}: {(!paused ? (object)"Disabling" : (object)"Pausing")} {(object)((object)behavior).ToString()}");
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

        public void BehaviorOnCollisionEnter(Collision collision, Behavior behavior)
        {
            if (!this.IsBehaviorEnabled(behavior))
            {
                return;
            }

            BehaviorManager.BehaviorTree behaviorTree = this.behaviorTreeMap[behavior];
            for (int index1 = 0; index1 < behaviorTree.activeStack.Count; ++index1)
            {
                if (behaviorTree.activeStack[index1].Count != 0)
                {
                    for (
                        int index2 = behaviorTree.activeStack[index1].Peek();
                        index2 != -1 && !behaviorTree.taskList[index2].Disabled;
                        index2 = behaviorTree.parentIndex[index2]
                    )
                    {
                        behaviorTree.taskList[index2].OnCollisionEnter(collision);
                    }
                }
            }

            for (int index3 = 0; index3 < behaviorTree.conditionalReevaluate.Count; ++index3)
            {
                int index4 = behaviorTree.conditionalReevaluate[index3].index;
                if (
                    !behaviorTree.taskList[index4].Disabled
                    && behaviorTree.conditionalReevaluate[index3].compositeIndex != -1
                )
                {
                    behaviorTree.taskList[index4].OnCollisionEnter(collision);
                }
            }
        }

        public void BehaviorOnCollisionExit(Collision collision, Behavior behavior)
        {
            if (!this.IsBehaviorEnabled(behavior))
            {
                return;
            }

            BehaviorManager.BehaviorTree behaviorTree = this.behaviorTreeMap[behavior];
            for (int index1 = 0; index1 < behaviorTree.activeStack.Count; ++index1)
            {
                if (behaviorTree.activeStack[index1].Count != 0)
                {
                    for (
                        int index2 = behaviorTree.activeStack[index1].Peek();
                        index2 != -1 && !behaviorTree.taskList[index2].Disabled;
                        index2 = behaviorTree.parentIndex[index2]
                    )
                    {
                        behaviorTree.taskList[index2].OnCollisionExit(collision);
                    }
                }
            }

            for (int index3 = 0; index3 < behaviorTree.conditionalReevaluate.Count; ++index3)
            {
                int index4 = behaviorTree.conditionalReevaluate[index3].index;
                if (
                    !behaviorTree.taskList[index4].Disabled
                    && behaviorTree.conditionalReevaluate[index3].compositeIndex != -1
                )
                {
                    behaviorTree.taskList[index4].OnCollisionExit(collision);
                }
            }
        }

        public void BehaviorOnTriggerEnter(Collider other, Behavior behavior)
        {
            if (!this.IsBehaviorEnabled(behavior))
            {
                return;
            }

            BehaviorManager.BehaviorTree behaviorTree = this.behaviorTreeMap[behavior];
            for (int index1 = 0; index1 < behaviorTree.activeStack.Count; ++index1)
            {
                if (behaviorTree.activeStack[index1].Count != 0)
                {
                    for (
                        int index2 = behaviorTree.activeStack[index1].Peek();
                        index2 != -1 && !behaviorTree.taskList[index2].Disabled;
                        index2 = behaviorTree.parentIndex[index2]
                    )
                        behaviorTree.taskList[index2].OnTriggerEnter(other);
                }
            }

            for (int index3 = 0; index3 < behaviorTree.conditionalReevaluate.Count; ++index3)
            {
                int index4 = behaviorTree.conditionalReevaluate[index3].index;
                if (
                    !behaviorTree.taskList[index4].Disabled
                    && behaviorTree.conditionalReevaluate[index3].compositeIndex != -1
                )
                    behaviorTree.taskList[index4].OnTriggerEnter(other);
            }
        }

        public void BehaviorOnTriggerExit(Collider other, Behavior behavior)
        {
            if (!this.IsBehaviorEnabled(behavior))
                return;
            BehaviorManager.BehaviorTree behaviorTree = this.behaviorTreeMap[behavior];
            for (int index1 = 0; index1 < behaviorTree.activeStack.Count; ++index1)
            {
                if (behaviorTree.activeStack[index1].Count != 0)
                {
                    for (
                        int index2 = behaviorTree.activeStack[index1].Peek();
                        index2 != -1 && !behaviorTree.taskList[index2].Disabled;
                        index2 = behaviorTree.parentIndex[index2]
                    )
                        behaviorTree.taskList[index2].OnTriggerExit(other);
                }
            }

            for (int index3 = 0; index3 < behaviorTree.conditionalReevaluate.Count; ++index3)
            {
                int index4 = behaviorTree.conditionalReevaluate[index3].index;
                if (
                    !behaviorTree.taskList[index4].Disabled
                    && behaviorTree.conditionalReevaluate[index3].compositeIndex != -1
                )
                    behaviorTree.taskList[index4].OnTriggerExit(other);
            }
        }

        public void BehaviorOnCollisionEnter2D(Collision2D collision, Behavior behavior)
        {
            if (!this.IsBehaviorEnabled(behavior))
                return;
            BehaviorManager.BehaviorTree behaviorTree = this.behaviorTreeMap[behavior];
            for (int index1 = 0; index1 < behaviorTree.activeStack.Count; ++index1)
            {
                if (behaviorTree.activeStack[index1].Count != 0)
                {
                    for (
                        int index2 = behaviorTree.activeStack[index1].Peek();
                        index2 != -1 && !behaviorTree.taskList[index2].Disabled;
                        index2 = behaviorTree.parentIndex[index2]
                    )
                        behaviorTree.taskList[index2].OnCollisionEnter2D(collision);
                }
            }

            for (int index3 = 0; index3 < behaviorTree.conditionalReevaluate.Count; ++index3)
            {
                int index4 = behaviorTree.conditionalReevaluate[index3].index;
                if (
                    !behaviorTree.taskList[index4].Disabled
                    && behaviorTree.conditionalReevaluate[index3].compositeIndex != -1
                )
                    behaviorTree.taskList[index4].OnCollisionEnter2D(collision);
            }
        }

        public void BehaviorOnCollisionExit2D(Collision2D collision, Behavior behavior)
        {
            if (!this.IsBehaviorEnabled(behavior))
                return;
            BehaviorManager.BehaviorTree behaviorTree = this.behaviorTreeMap[behavior];
            for (int index1 = 0; index1 < behaviorTree.activeStack.Count; ++index1)
            {
                if (behaviorTree.activeStack[index1].Count != 0)
                {
                    for (
                        int index2 = behaviorTree.activeStack[index1].Peek();
                        index2 != -1 && !behaviorTree.taskList[index2].Disabled;
                        index2 = behaviorTree.parentIndex[index2]
                    )
                        behaviorTree.taskList[index2].OnCollisionExit2D(collision);
                }
            }

            for (int index3 = 0; index3 < behaviorTree.conditionalReevaluate.Count; ++index3)
            {
                int index4 = behaviorTree.conditionalReevaluate[index3].index;
                if (
                    !behaviorTree.taskList[index4].Disabled
                    && behaviorTree.conditionalReevaluate[index3].compositeIndex != -1
                )
                    behaviorTree.taskList[index4].OnCollisionExit2D(collision);
            }
        }

        public void BehaviorOnTriggerEnter2D(Collider2D other, Behavior behavior)
        {
            if (!this.IsBehaviorEnabled(behavior))
                return;
            BehaviorManager.BehaviorTree behaviorTree = this.behaviorTreeMap[behavior];
            for (int index1 = 0; index1 < behaviorTree.activeStack.Count; ++index1)
            {
                if (behaviorTree.activeStack[index1].Count != 0)
                {
                    for (
                        int index2 = behaviorTree.activeStack[index1].Peek();
                        index2 != -1 && !behaviorTree.taskList[index2].Disabled;
                        index2 = behaviorTree.parentIndex[index2]
                    )
                        behaviorTree.taskList[index2].OnTriggerEnter2D(other);
                }
            }

            for (int index3 = 0; index3 < behaviorTree.conditionalReevaluate.Count; ++index3)
            {
                int index4 = behaviorTree.conditionalReevaluate[index3].index;
                if (
                    !behaviorTree.taskList[index4].Disabled
                    && behaviorTree.conditionalReevaluate[index3].compositeIndex != -1
                )
                    behaviorTree.taskList[index4].OnTriggerEnter2D(other);
            }
        }

        public void BehaviorOnTriggerExit2D(Collider2D other, Behavior behavior)
        {
            if (!this.IsBehaviorEnabled(behavior))
                return;
            BehaviorManager.BehaviorTree behaviorTree = this.behaviorTreeMap[behavior];
            for (int index1 = 0; index1 < behaviorTree.activeStack.Count; ++index1)
            {
                if (behaviorTree.activeStack[index1].Count != 0)
                {
                    for (
                        int index2 = behaviorTree.activeStack[index1].Peek();
                        index2 != -1 && !behaviorTree.taskList[index2].Disabled;
                        index2 = behaviorTree.parentIndex[index2]
                    )
                        behaviorTree.taskList[index2].OnTriggerExit2D(other);
                }
            }

            for (int index3 = 0; index3 < behaviorTree.conditionalReevaluate.Count; ++index3)
            {
                int index4 = behaviorTree.conditionalReevaluate[index3].index;
                if (
                    !behaviorTree.taskList[index4].Disabled
                    && behaviorTree.conditionalReevaluate[index3].compositeIndex != -1
                )
                    behaviorTree.taskList[index4].OnTriggerExit2D(other);
            }
        }

        public void BehaviorOnControllerColliderHit(ControllerColliderHit hit, Behavior behavior)
        {
            if (!this.IsBehaviorEnabled(behavior))
                return;
            BehaviorManager.BehaviorTree behaviorTree = this.behaviorTreeMap[behavior];
            for (int index1 = 0; index1 < behaviorTree.activeStack.Count; ++index1)
            {
                if (behaviorTree.activeStack[index1].Count != 0)
                {
                    for (
                        int index2 = behaviorTree.activeStack[index1].Peek();
                        index2 != -1 && !behaviorTree.taskList[index2].Disabled;
                        index2 = behaviorTree.parentIndex[index2]
                    )
                        behaviorTree.taskList[index2].OnControllerColliderHit(hit);
                }
            }

            for (int index3 = 0; index3 < behaviorTree.conditionalReevaluate.Count; ++index3)
            {
                int index4 = behaviorTree.conditionalReevaluate[index3].index;
                if (
                    !behaviorTree.taskList[index4].Disabled
                    && behaviorTree.conditionalReevaluate[index3].compositeIndex != -1
                )
                    behaviorTree.taskList[index4].OnControllerColliderHit(hit);
            }
        }

        public void BehaviorOnAnimatorIK(Behavior behavior)
        {
            if (!this.IsBehaviorEnabled(behavior))
                return;
            BehaviorManager.BehaviorTree behaviorTree = this.behaviorTreeMap[behavior];
            for (int index1 = 0; index1 < behaviorTree.activeStack.Count; ++index1)
            {
                if (behaviorTree.activeStack[index1].Count != 0)
                {
                    for (
                        int index2 = behaviorTree.activeStack[index1].Peek();
                        index2 != -1 && !behaviorTree.taskList[index2].Disabled;
                        index2 = behaviorTree.parentIndex[index2]
                    )
                        behaviorTree.taskList[index2].OnAnimatorIK();
                }
            }

            for (int index3 = 0; index3 < behaviorTree.conditionalReevaluate.Count; ++index3)
            {
                int index4 = behaviorTree.conditionalReevaluate[index3].index;
                if (
                    !behaviorTree.taskList[index4].Disabled
                    && behaviorTree.conditionalReevaluate[index3].compositeIndex != -1
                )
                    behaviorTree.taskList[index4].OnAnimatorIK();
            }
        }

        private BehaviorManager.BehaviorTree LoadBehavior(
            Behavior behavior,
            GameObject behaviorGameObject,
            string gameObjectName,
            Transform behaviorTransform
        )
        {
            BehaviorManager.BehaviorTree behaviorTree =
                ObjectPool.Get<BehaviorManager.BehaviorTree>();
            if (behaviorTree == null)
            {
                Debug.LogError($"Create behavior tree from pool failure !");
            }

            lock ((object)this.lockObject)
            {
                behavior.CheckForSerialization(false, true);
            }

            Task rootTask = behavior.GetBehaviorSource().RootTask;
            if (rootTask == null)
            {
                behaviorTree.errorState =
                    $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{(object)gameObjectName}\" contains no root task. This behavior will be disabled.";
                return behaviorTree;
            }

            behaviorTree.Initialize(behavior);
            behaviorTree.parentIndex.Add(-1);
            behaviorTree.relativeChildIndex.Add(-1);
            behaviorTree.parentCompositeIndex.Add(-1);
            BehaviorManager.TaskAddData data = ObjectPool.Get<BehaviorManager.TaskAddData>();
            data.Initialize();
            bool hasExternalBehavior =
                (UnityEngine.Object)behavior.ExternalBehavior != (UnityEngine.Object)null;
            int taskList = this.AddToTaskList(behaviorTree,
                rootTask,
                behaviorGameObject,
                behaviorTransform,
                ref hasExternalBehavior,
                data);
            if (taskList < 0)
            {
                switch (taskList + 6)
                {
                    case 0:
                        behaviorTree.errorState =
                            $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{(object)gameObjectName}\" contains a root task which is disabled. This behavior will be disabled.";
                        break;
                    case 1:
                        behaviorTree.errorState =
                            $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{(object)gameObjectName}\" contains a Behavior Tree Reference task ({(object)data.errorTaskName} (index {(object)data.errorTask})) that which has an element with a null value in the externalBehaviors array. This behavior will be disabled.";
                        break;
                    case 2:
                        behaviorTree.errorState =
                            $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{(object)behaviorGameObject.name}\" contains multiple external behavior trees at the root task or as a child of a parent task which cannot contain so many children (such as a decorator task). This behavior will be disabled.";
                        break;
                    case 3:
                        behaviorTree.errorState =
                            $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{(object)gameObjectName}\" contains a null task (referenced from parent task {(object)data.errorTaskName} (index {(object)data.errorTask})). This behavior will be disabled.";
                        break;
                    case 4:
                        behaviorTree.errorState =
                            $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{(object)gameObjectName}\" cannot find the referenced external task. This behavior will be disabled.";
                        break;
                    case 5:
                        behaviorTree.errorState =
                            $"The behavior \"{(object)behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{(object)gameObjectName}\" contains a parent task ({(object)data.errorTaskName} (index {(object)data.errorTask})) with no children. This behavior will be disabled.";
                        break;
                }
            }

            data.Destroy();
            ObjectPool.Return<BehaviorManager.TaskAddData>(data);
            return behaviorTree;
        }

        private int AddToTaskList(BehaviorTree behaviorTree,
            Task task,
            GameObject behaviorGameObject,
            Transform behaviorTransform,
            ref bool hasExternalBehavior,
            TaskAddData data
        )
        {
            if (task == null)
            {
                return -3;
            }

            task.GameObject = behaviorGameObject;
            task.Transform = behaviorTransform;
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
                        behaviorSourceArray[index1].CheckForSerialization(true,
                            behaviorSource,
                            true);
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
                                        !data.overrideFields.ContainsKey(behaviorReference.variables[index2].Value.name)
                                    )
                                    {
                                        BehaviorManager.TaskAddData.OverrideFieldValue overrideFieldValue1 =
                                            ObjectPool.Get<BehaviorManager.TaskAddData.OverrideFieldValue>();
                                        overrideFieldValue1.Initialize((object)behaviorReference.variables[index2].Value,
                                            data.depth);
                                        if (behaviorReference.variables[index2].Value != null)
                                        {
                                            NamedVariable namedVariable = behaviorReference
                                                .variables[index2]
                                                .Value;
                                            if (string.IsNullOrEmpty(namedVariable.name))
                                            {
                                                UnityEngine
                                                    .Debug
                                                    .LogWarning($"Warning: Named variable on reference task {behaviorReference.FriendlyName} (id {(object)behaviorReference.ID}) is null");
                                                continue;
                                            }

                                            if (namedVariable.value != null)
                                            {
                                                BehaviorManager.TaskAddData.OverrideFieldValue overrideFieldValue2;
                                                if (
                                                    data.overrideFields.TryGetValue(namedVariable.name,
                                                        out overrideFieldValue2)
                                                )
                                                    overrideFieldValue1 = overrideFieldValue2;
                                                else if (
                                                    !string.IsNullOrEmpty(namedVariable.value.Name)
                                                    && data.overrideFields.TryGetValue(namedVariable.value.Name,
                                                        out overrideFieldValue2)
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
                                                        .LogWarning($"Warning: Named variable on reference task {behaviorReference.FriendlyName} (id {(object)behaviorReference.ID}) is null");
                                                    continue;
                                                }

                                                BehaviorManager.TaskAddData.OverrideFieldValue overrideFieldValue3;
                                                if (
                                                    data.overrideFields.TryGetValue(genericVariable.value.Name,
                                                        out overrideFieldValue3)
                                                )
                                                    overrideFieldValue1 = overrideFieldValue3;
                                            }
                                        }

                                        data.overrideFields.Add(behaviorReference.variables[index2].Value.name,
                                            overrideFieldValue1);
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
                                taskList = this.AddToTaskList(behaviorTree,
                                    rootTask,
                                    behaviorGameObject,
                                    behaviorTransform,
                                    ref hasExternalBehavior,
                                    data)
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
                            task.NodeData.Offset = (float2)data.offset;
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
                        data.errorTaskName = string.IsNullOrEmpty(behaviorTree.taskList[data.errorTask].FriendlyName)
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
                                taskList = this.AddToTaskList(behaviorTree,
                                    parentTask.Children[index5],
                                    behaviorGameObject,
                                    behaviorTransform,
                                    ref hasExternalBehavior,
                                    data)
                            ) < 0
                        )
                        {
                            if (taskList == -3)
                            {
                                data.errorTask = index4;
                                data.errorTaskName = string.IsNullOrEmpty(behaviorTree.taskList[data.errorTask].FriendlyName)
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
        
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            BehaviorManager.instance = (BehaviorManager)null;
        }
        
        private class BehaviorThreadLoader
        {
            private Behavior behavior;

            private GameObject gameObject;

            private string gameObjectName;

            private Transform transform;

            private Func<
                Behavior,
                GameObject,
                string,
                Transform,
                BehaviorManager.BehaviorTree
            > loadBehaviorAction;

            private Thread thread;

            private BehaviorManager.BehaviorTree behaviorTree;

            public Behavior Behavior => this.behavior;

            public Thread Thread
            {
                get => this.thread;
                set => this.thread = value;
            }

            public BehaviorTree BehaviorTree => this.behaviorTree;

            public BehaviorThreadLoader(
                Behavior behavior,
                Func<Behavior, GameObject, string, Transform, BehaviorManager.BehaviorTree> action
            )
            {
                this.behavior = behavior;
                this.gameObject = this.behavior.gameObject;
                this.gameObjectName = this.gameObject.name;
                this.transform = this.behavior.transform;
                this.loadBehaviorAction = action;
            }

            public void LoadBehavior()
            {
                this.behaviorTree = this.loadBehaviorAction(this.behavior,
                    this.gameObject,
                    this.gameObjectName,
                    this.transform);
            }
        }

        private IEnumerator CheckThreadLoaders()
        {
            while (this.activeThreads.Count > 0)
            {
                for (int i = this.activeThreads.Count - 1; i >= 0; i--)
                {
                    var activeThread = this.activeThreads[i];
                    if (!activeThread.Thread.IsAlive)
                    {
                        this.LoadBehaviorComplete(activeThread.Behavior, activeThread.BehaviorTree);
                        this.activeThreads.RemoveAt(i);
                    }
                }

                if (this.activeThreads.Count == 0)
                {
                    yield break;
                }

                yield return new WaitForEndOfFrame();
            }
        }
    }
}