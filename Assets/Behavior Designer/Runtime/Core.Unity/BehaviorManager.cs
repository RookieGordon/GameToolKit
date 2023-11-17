using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BehaviorDesigner.Runtime.Tasks;
using UnityEngine;
using Task = BehaviorDesigner.Runtime.Tasks.Task;

namespace BehaviorDesigner.Runtime
{
    [AddComponentMenu("Behavior Designer/Behavior Manager")]
    public partial class BehaviorManager : MonoBehaviour
    {
        [SerializeField]
        private UpdateIntervalType updateInterval;

        [SerializeField]
        private float updateIntervalSeconds;

        [SerializeField]
        private BehaviorManager.ExecutionsPerTickType executionsPerTick;

        [SerializeField]
        private int maxTaskExecutionsPerTick = 100;

        private WaitForSeconds updateWait;

        private UnityEngine.Object lockObject = new UnityEngine.Object();

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

        private void SetTaskGameObject(Task task, Behavior behavior)
        {
            task.GameObject = behavior.gameObject;
            task.Transform = behavior.transform;
        }

        public void EnableBehavior(Behavior behavior)
        {
            if (this.IsBehaviorEnabled(behavior))
            {
                return;
            }
            BehaviorManager.BehaviorTree behaviorTree;
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
                BehaviorThreadLoader behaviorThreadLoader = new BehaviorThreadLoader(
                    behavior,
                    new Func<Behavior, BehaviorTree>(this.LoadBehavior)
                );
                Thread thread = new Thread(new ThreadStart(behaviorThreadLoader.LoadBehavior));
                behaviorThreadLoader.Thread = thread;
                thread.Start();
                if (this.activeThreads == null)
                {
                    this.activeThreads = new List<BehaviorThreadLoader>();
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
                behaviorTree = this.LoadBehavior(behavior);
                this.LoadBehaviorComplete(behavior, behaviorTree);
            }
        }

        private BehaviorTree LoadBehavior(
            Behavior behavior,
            GameObject behaviorGameObject,
            string gameObjectName,
            Transform behaviorTransform
        )
        {
            BehaviorTree behaviorTree = ObjectPool.Get<BehaviorTree>();
            object lockObject = this.lockObject;
            lock (lockObject)
            {
                behavior.CheckForSerialization(false, true);
            }
            Task rootTask = behavior.GetBehaviorSource().RootTask;
            if (rootTask == null)
            {
                behaviorTree.errorState =
                    $"The behavior \"{behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{gameObjectName}\" contains no root task. This behavior will be disabled.";
                return behaviorTree;
            }
            behaviorTree.Initialize(behavior);
            behaviorTree.parentIndex.Add(-1);
            behaviorTree.relativeChildIndex.Add(-1);
            behaviorTree.parentCompositeIndex.Add(-1);
            TaskAddData data = ObjectPool.Get<TaskAddData>();
            data.Initialize();
            bool hasExternalBehavior = behavior.ExternalBehavior != null;
            int num = this.AddToTaskList(
                behaviorTree,
                rootTask,
                behaviorGameObject,
                behaviorTransform,
                ref hasExternalBehavior,
                data
            );
            if (num < 0)
            {
                switch (num)
                {
                    case -6:
                        behaviorTree.errorState =
                            $"The behavior \"{behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{gameObjectName}\" contains a root task which is disabled. This behavior will be disabled.";
                        break;

                    case -5:
                        behaviorTree.errorState =
                            $"The behavior \"{behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{gameObjectName}\" contains a Behavior Tree Reference task ({data.errorTaskName} (index {data.errorTask})) that which has an element with a null value in the externalBehaviors array. This behavior will be disabled.";
                        break;

                    case -4:
                        behaviorTree.errorState =
                            $"The behavior \"{behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{behaviorGameObject.name}\" contains multiple external behavior trees at the root task or as a child of a parent task which cannot contain so many children (such as a decorator task). This behavior will be disabled.";
                        break;

                    case -3:
                        behaviorTree.errorState =
                            $"The behavior \"{behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{gameObjectName}\" contains a null task (referenced from parent task {data.errorTaskName} (index {data.errorTask})). This behavior will be disabled.";
                        break;

                    case -2:
                        behaviorTree.errorState =
                            $"The behavior \"{behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{gameObjectName}\" cannot find the referenced external task. This behavior will be disabled.";
                        break;

                    case -1:
                        behaviorTree.errorState =
                            $"The behavior \"{behavior.GetBehaviorSource().behaviorName}\" on GameObject \"{gameObjectName}\" contains a parent task ({data.errorTaskName} (index {data.errorTask})) with no children. This behavior will be disabled.";
                        break;

                    default:
                        break;
                }
            }
            data.Destroy();
            ObjectPool.Return<TaskAddData>(data);
            return behaviorTree;
        }

        private class BehaviorThreadLoader
        {
            private Behavior behavior;
            private Func<Behavior, BehaviorTree> loadBehaviorAction;
            private Thread thread;
            private BehaviorTree behaviorTree;

            public BehaviorThreadLoader(Behavior behaviorTree, Func<Behavior, BehaviorTree> action)
            {
                this.behavior = behaviorTree;
                this.loadBehaviorAction = action;
            }

            public Behavior Behavior => this.behavior;

            public Thread Thread
            {
                get => this.thread;
                set => this.thread = value;
            }

            public BehaviorTree BehaviorTree => this.behaviorTree;

            public void LoadBehavior() =>
                this.behaviorTree = this.loadBehaviorAction(this.behavior);
        }

        [DebuggerHidden]
        private IEnumerator CheckThreadLoaders()
        {
            for (int i = this.activeThreads.Count - 1; i >= 0; i--)
            {
                var activeThread = this.activeThreads[i];
                if (activeThread.Thread.IsAlive)
                {
                    this.LoadBehaviorComplete(activeThread.Behavior, activeThread.BehaviorTree);
                    this.activeThreads.RemoveAt(i);
                    yield return new WaitForEndOfFrame();
                }
            }
        }
    }
}
