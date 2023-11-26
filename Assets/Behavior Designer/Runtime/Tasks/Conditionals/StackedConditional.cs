using Debug = BehaviorDesigner.Runtime.BehaviorDebug;

namespace BehaviorDesigner.Runtime.Tasks
{
    [TaskDescription("Allows multiple conditional tasks to be added to a single node.")]
    [TaskIcon("{SkinColor}StackedConditionalIcon.png")]
    public partial class StackedConditional : Conditional
    {
        [InspectTask]
        public Conditional[] conditionals;
        public enum ComparisonType
        {
            Sequence,
            Selector
        }
        [Tooltip("Specifies if the tasks should be traversed with an AND (Sequence) or an OR (Selector).")]
        public ComparisonType comparisonType;
        [Tooltip("Should the tasks be labeled within the graph?")]
        public bool graphLabel;

        public override void OnAwake()
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
#if UNITY_PLATFORM
                conditionals[i].GameObject = gameObject;
                conditionals[i].Transform = transform;
#endif
                conditionals[i].Owner = Owner;
#if UNITY_EDITOR || DLL_RELEASE || DLL_DEBUG
                conditionals[i].NodeData = new NodeData();
#endif
                conditionals[i].OnAwake();
            }
        }

        public override void OnStart()
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].OnStart();
            }
        }

        public override TaskStatus OnUpdate()
        {
            if (conditionals == null) {
                return TaskStatus.Failure;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                var executionStatus = conditionals[i].OnUpdate();
#if UNITY_PLATFORM || DLL_RELEASE || DLL_DEBUG
                conditionals[i].NodeData.ExecutionStatus = executionStatus;
                if (conditionals[i].NodeData.ExecutionStatus == TaskStatus.Running) {
                    Debug.LogWarning("Warning: The conditional task returned a status of running when conditional tasks should only return success or failure.");
                }
#endif
                if (comparisonType == ComparisonType.Sequence && executionStatus == TaskStatus.Failure) {
                    return TaskStatus.Failure;
                } else if (comparisonType == ComparisonType.Selector && executionStatus == TaskStatus.Success) {
                    return TaskStatus.Success;
                }
            }
            return comparisonType == ComparisonType.Sequence ? TaskStatus.Success : TaskStatus.Failure;
        }

        public override void OnFixedUpdate()
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].OnFixedUpdate();
            }
        }

        public override void OnLateUpdate()
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].OnLateUpdate();
            }
        }

        public override void OnEnd()
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].OnEnd();
            }
        }

        public override string OnDrawNodeText()
        {
            if (conditionals == null || !graphLabel) {
                return string.Empty;
            }

            var text = string.Empty;
            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                if (!string.IsNullOrEmpty(text)) {
                    text += "\n";
                }
                text += conditionals[i].GetType().Name;
            }

            return text;
        }

        public override void OnReset()
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].OnReset();
            }
        }
    }
}