using Unity.Mathematics;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityVector3
{
    [TaskCategory("Unity/Vector3")]
    [TaskDescription("Stores the square magnitude of the Vector3.")]
    public class GetSqrMagnitude : Action
    {
        [Tooltip("The Vector3 to get the square magnitude of")]
        public SharedVector3 vector3Variable;

        [Tooltip("The square magnitude of the vector")] [RequiredField]
        public SharedFloat storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = math.lengthsq(vector3Variable.Value);
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            vector3Variable = float3.zero;
            storeResult = 0;
        }
    }
}