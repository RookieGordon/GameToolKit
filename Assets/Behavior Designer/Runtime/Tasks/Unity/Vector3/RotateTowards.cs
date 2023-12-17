using Unity.Mathematics;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityVector3
{
    [TaskCategory("Unity/Vector3")]
    [TaskDescription("Rotate the current rotation to the target rotation.")]
    public class RotateTowards : Action
    {
        [Tooltip("The current rotation in euler angles")]
        public SharedVector3 currentRotation;

        [Tooltip("The target rotation in euler angles")]
        public SharedVector3 targetRotation;

        [Tooltip("The maximum delta of the degrees")]
        public SharedFloat maxDegreesDelta;

        [Tooltip("The maximum delta of the magnitude")]
        public SharedFloat maxMagnitudeDelta;

        [Tooltip("The rotation resut")] [RequiredField]
        public SharedVector3 storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = MathUtils.RotateTowards(currentRotation.Value, targetRotation.Value, maxDegreesDelta.Value * math.radians(TimeUtils.deltaTime), maxMagnitudeDelta.Value);
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            currentRotation = float3.zero;
            targetRotation = float3.zero;
            storeResult = float3.zero;
            maxDegreesDelta = 0;
            maxMagnitudeDelta = 0;
        }
    }
}