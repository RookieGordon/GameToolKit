using Unity.Mathematics;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityQuaternion
{
    [TaskCategory("Unity/Quaternion")]
    [TaskDescription("Stores the angle in degrees between two rotations.")]
    public class Angle : Action
    {
        [Tooltip("The first rotation")] public SharedQuaternion firstRotation;
        [Tooltip("The second rotation")] public SharedQuaternion secondRotation;

        [Tooltip("The stored result")] [RequiredField]
        public SharedFloat storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = MathUtils.GetQuaternionAngle(firstRotation.Value, secondRotation.Value);
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            firstRotation = secondRotation = quaternion.identity;
            storeResult = 0;
        }
    }
}