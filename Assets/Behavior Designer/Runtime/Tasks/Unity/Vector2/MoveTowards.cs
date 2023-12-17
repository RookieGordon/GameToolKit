using Unity.Mathematics;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityVector2
{
    [TaskCategory("Unity/Vector2")]
    [TaskDescription("Move from the current position to the target position.")]
    public class MoveTowards : Action
    {
        [Tooltip("The current position")] public SharedVector2 currentPosition;
        [Tooltip("The target position")] public SharedVector2 targetPosition;
        [Tooltip("The movement speed")] public SharedFloat speed;

        [Tooltip("The move resut")] [RequiredField]
        public SharedVector2 storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = MathUtils.MoveTowards(currentPosition.Value, targetPosition.Value, speed.Value * TimeUtils.deltaTime);
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            currentPosition = float2.zero;
            targetPosition = float2.zero;
            storeResult = float2.zero;
            speed = 0;
        }
    }
}