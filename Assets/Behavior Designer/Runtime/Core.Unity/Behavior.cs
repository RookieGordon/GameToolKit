using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BehaviorDesigner.Runtime.Tasks;
using UnityEngine;
using Tooltip = UnityEngine.TooltipAttribute;

namespace BehaviorDesigner.Runtime
{
    public abstract partial class Behavior : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("If true, the behavior tree will start running when the component is enabled.")]
        private bool startWhenEnabled = true;

        [SerializeField]
        [Tooltip(
            "Specifies if the behavior tree should load in a separate thread.Because Unity does not allow for API calls to be made on worker threads this option should be disabled if you are using property mappingsfor the shared variables."
        )]
        private bool asynchronousLoad;

        [SerializeField]
        [Tooltip(
            "If true, the behavior tree will pause when the component is disabled. If false, the behavior tree will end."
        )]
        private bool pauseWhenDisabled;

        [SerializeField]
        [Tooltip(
            "If true, the behavior tree will restart from the beginning when it has completed execution. If false, the behavior tree will end."
        )]
        private bool restartWhenComplete;

        [SerializeField]
        [Tooltip(
            "Used for debugging. If enabled, the behavior tree will output any time a task status changes, such as it starting or stopping."
        )]
        private bool logTaskChanges;

        [SerializeField]
        [Tooltip(
            "A numerical grouping of behavior trees. Can be used to easily find behavior trees."
        )]
        private int group;

        [SerializeField]
        [Tooltip(
            "If true, the variables and task public variables will be reset to their original values when the tree restarts."
        )]
        private bool resetValuesOnRestart;

        [SerializeField]
        [Tooltip(
            "A field to specify the external behavior tree that should be run when this behavior tree starts."
        )]
        private ExternalBehavior externalBehavior;

        [SerializeField]
        private BehaviorSource mBehaviorSource;

        public Behavior.GizmoViewMode gizmoViewMode;

        public UnityEngine.Object GetObject()
        {
            return (UnityEngine.Object)this;
        }

        public string GetOwnerName()
        {
            return this.gameObject.name;
        }

        public void CheckForSerialization()
        {
            this.CheckForSerialization(false, Application.isPlaying);
        }

        int IBehavior.GetInstanceID()
        {
            return this.GetInstanceID();
        }

        public void SetUnityObject()
        {
            this.behaviorName = this.gameObject.name;
            this.instanceID = this.GetInstanceID();
        }

        // TODO 这几个Coroutine如果是加载相关的，能不能不用改？
        public Coroutine StartTaskCoroutine(Task task, string methodName)
        {
            MethodInfo method = task.GetType()
                .GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
            if (method == (MethodInfo)null)
            {
                Debug.LogError($"Unable to start coroutine {methodName}: method not found");
                return (Coroutine)null;
            }

            if (this.activeTaskCoroutines == null)
                this.activeTaskCoroutines = new Dictionary<string, List<TaskCoroutine>>();
            TaskCoroutine taskCoroutine = new TaskCoroutine(
                this,
                (IEnumerator)method.Invoke((object)task, new object[0]),
                methodName
            );
            if (this.activeTaskCoroutines.ContainsKey(methodName))
            {
                List<TaskCoroutine> activeTaskCoroutine = this.activeTaskCoroutines[methodName];
                activeTaskCoroutine.Add(taskCoroutine);
                this.activeTaskCoroutines[methodName] = activeTaskCoroutine;
            }
            else
                this.activeTaskCoroutines.Add(
                    methodName,
                    new List<TaskCoroutine>() { taskCoroutine }
                );

            return taskCoroutine.Coroutine;
        }

        public Coroutine StartTaskCoroutine(Task task, string methodName, object value)
        {
            MethodInfo method = task.GetType()
                .GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
            if (method == (MethodInfo)null)
            {
                Debug.LogError(
                    (object)("Unable to start coroutine " + methodName + ": method not found")
                );
                return (Coroutine)null;
            }

            if (this.activeTaskCoroutines == null)
            {
                this.activeTaskCoroutines = new Dictionary<string, List<TaskCoroutine>>();
            }
            TaskCoroutine taskCoroutine = new TaskCoroutine(
                this,
                (IEnumerator)method.Invoke((object)task, new object[1] { value }),
                methodName
            );
            if (this.activeTaskCoroutines.ContainsKey(methodName))
            {
                List<TaskCoroutine> activeTaskCoroutine = this.activeTaskCoroutines[methodName];
                activeTaskCoroutine.Add(taskCoroutine);
                this.activeTaskCoroutines[methodName] = activeTaskCoroutine;
            }
            else
                this.activeTaskCoroutines.Add(
                    methodName,
                    new List<TaskCoroutine>() { taskCoroutine }
                );

            return taskCoroutine.Coroutine;
        }

        public void StopTaskCoroutine(string methodName)
        {
            if (!this.activeTaskCoroutines.ContainsKey(methodName))
            {
                return;
            }
            List<TaskCoroutine> activeTaskCoroutine = this.activeTaskCoroutines[methodName];
            for (int index = 0; index < activeTaskCoroutine.Count; ++index)
                activeTaskCoroutine[index].Stop();
        }

        public void StopAllTaskCoroutines()
        {
            this.StopAllCoroutines();
            if (this.activeTaskCoroutines == null)
            {
                return;
            }
            foreach (
                KeyValuePair<
                    string,
                    List<TaskCoroutine>
                > activeTaskCoroutine in this.activeTaskCoroutines
            )
            {
                List<TaskCoroutine> taskCoroutineList = activeTaskCoroutine.Value;
                for (int index = 0; index < taskCoroutineList.Count; ++index)
                {
                    taskCoroutineList[index].Stop();
                }
            }
        }

        public void TaskCoroutineEnded(TaskCoroutine taskCoroutine, string coroutineName)
        {
            if (!this.activeTaskCoroutines.ContainsKey(coroutineName))
            {
                return;
            }
            List<TaskCoroutine> activeTaskCoroutine = this.activeTaskCoroutines[coroutineName];
            if (activeTaskCoroutine.Count == 1)
            {
                this.activeTaskCoroutines.Remove(coroutineName);
            }
            else
            {
                activeTaskCoroutine.Remove(taskCoroutine);
                this.activeTaskCoroutines[coroutineName] = activeTaskCoroutine;
            }
        }

        public void OnCollisionEnter(Collision collision)
        {
            if (
                !this.hasEvent[0]
                || !((UnityEngine.Object)BehaviorManager.instance != (UnityEngine.Object)null)
            )
            {
                return;
            }
            BehaviorManager.instance.BehaviorOnCollisionEnter(collision, this);
        }

        public void OnCollisionExit(Collision collision)
        {
            if (
                !this.hasEvent[1]
                || !((UnityEngine.Object)BehaviorManager.instance != (UnityEngine.Object)null)
            )
            {
                return;
            }
            BehaviorManager.instance.BehaviorOnCollisionExit(collision, this);
        }

        public void OnTriggerEnter(Collider other)
        {
            if (
                !this.hasEvent[2]
                || !((UnityEngine.Object)BehaviorManager.instance != (UnityEngine.Object)null)
            )
            {
                return;
            }
            BehaviorManager.instance.BehaviorOnTriggerEnter(other, this);
        }

        public void OnTriggerExit(Collider other)
        {
            if (
                !this.hasEvent[3]
                || !((UnityEngine.Object)BehaviorManager.instance != (UnityEngine.Object)null)
            )
            {
                return;
            }
            BehaviorManager.instance.BehaviorOnTriggerExit(other, this);
        }

        public void OnCollisionEnter2D(Collision2D collision)
        {
            if (
                !this.hasEvent[4]
                || !((UnityEngine.Object)BehaviorManager.instance != (UnityEngine.Object)null)
            )
            {
                return;
            }
            BehaviorManager.instance.BehaviorOnCollisionEnter2D(collision, this);
        }

        public void OnCollisionExit2D(Collision2D collision)
        {
            if (
                !this.hasEvent[5]
                || !((UnityEngine.Object)BehaviorManager.instance != (UnityEngine.Object)null)
            )
            {
                return;
            }
            BehaviorManager.instance.BehaviorOnCollisionExit2D(collision, this);
        }

        public void OnTriggerEnter2D(Collider2D other)
        {
            if (
                !this.hasEvent[6]
                || !((UnityEngine.Object)BehaviorManager.instance != (UnityEngine.Object)null)
            )
            {
                return;
            }
            BehaviorManager.instance.BehaviorOnTriggerEnter2D(other, this);
        }

        public void OnTriggerExit2D(Collider2D other)
        {
            if (
                !this.hasEvent[7]
                || !((UnityEngine.Object)BehaviorManager.instance != (UnityEngine.Object)null)
            )
            {
                return;
            }
            BehaviorManager.instance.BehaviorOnTriggerExit2D(other, this);
        }

        public void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (
                !this.hasEvent[8]
                || !((UnityEngine.Object)BehaviorManager.instance != (UnityEngine.Object)null)
            )
            {
                return;
            }
            BehaviorManager.instance.BehaviorOnControllerColliderHit(hit, this);
        }

        public void OnAnimatorIK()
        {
            if (
                !this.hasEvent[11]
                || !((UnityEngine.Object)BehaviorManager.instance != (UnityEngine.Object)null)
            )
            {
                return;
            }
            BehaviorManager.instance.BehaviorOnAnimatorIK(this);
        }

        public void OnDrawGizmos()
        {
            this.DrawTaskGizmos(false);
        }

        public void OnDrawGizmosSelected()
        {
            if (this.showBehaviorDesignerGizmo)
            {
                Gizmos.DrawIcon(this.transform.position, "Behavior Designer Scene Icon.png");
            }
            this.DrawTaskGizmos(true);
        }

        private void DrawTaskGizmos(bool selected)
        {
            if (
                this.gizmoViewMode == Behavior.GizmoViewMode.Never
                || this.gizmoViewMode == Behavior.GizmoViewMode.Selected && !selected
                || this.gizmoViewMode != Behavior.GizmoViewMode.Running
                    && this.gizmoViewMode != Behavior.GizmoViewMode.Always
                    && (!Application.isPlaying || this.ExecutionStatus != TaskStatus.Running)
                    && Application.isPlaying
            )
            {
                return;
            }
            this.CheckForSerialization();
            this.DrawTaskGizmos(this.mBehaviorSource.RootTask);
            List<Task> detachedTasks = this.mBehaviorSource.DetachedTasks;
            if (detachedTasks == null)
            {
                return;
            }
            for (int index = 0; index < detachedTasks.Count; ++index)
            {
                this.DrawTaskGizmos(detachedTasks[index]);
            }
        }

        private void DrawTaskGizmos(Task task)
        {
            if (
                task == null
                || this.gizmoViewMode == Behavior.GizmoViewMode.Running
                    && !task.NodeData.IsReevaluating
                    && (
                        task.NodeData.IsReevaluating
                        || task.NodeData.ExecutionStatus != TaskStatus.Running
                    )
            )
            {
                return;
            }
            task.OnDrawGizmos();
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
                this.DrawTaskGizmos(parentTask.Children[index]);
            }
        }

        public override string ToString()
        {
            return this.mBehaviorSource.ToString();
        }

        public static BehaviorManager CreateBehaviorManager()
        {
            if (BehaviorManager.instance == null && Application.isPlaying)
            {
                GameObject gameObject = new GameObject();
                gameObject.name = "Behavior Manager";
                return gameObject.AddComponent<BehaviorManager>();
            }
            return (BehaviorManager)null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            Behavior[] objectsOfType = UnityEngine.Object.FindObjectsOfType<Behavior>();
            if (objectsOfType == null)
            {
                return;
            }
            for (int index = 0; index < objectsOfType.Length; ++index)
            {
                objectsOfType[index].mBehaviorSource.HasSerialized = false;
            }
        }
    }
}
