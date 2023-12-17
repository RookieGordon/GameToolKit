using Unity.Mathematics;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityQuaternion
{
    [TaskCategory("Unity/Quaternion")]
    [TaskDescription("Stores a rotation which rotates from the first direction to the second.")]
    public class FromToRotation : Action
    {
        [Tooltip("The from rotation")] public SharedVector3 fromDirection;
        [Tooltip("The to rotation")] public SharedVector3 toDirection;

        [Tooltip("The stored result")] [RequiredField]
        public SharedQuaternion storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = MathUtils.FromToRotation(fromDirection.Value, toDirection.Value);
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            fromDirection = toDirection = float3.zero;
            storeResult = quaternion.identity;
        }
    }
}