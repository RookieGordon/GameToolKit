using Unity.Mathematics;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityVector3
{
    [TaskCategory("Unity/Vector3")]
    [TaskDescription("Stores the magnitude of the Vector3.")]
    public class GetMagnitude : Action
    {
        [Tooltip("The Vector3 to get the magnitude of")]
        public SharedVector3 vector3Variable;

        [Tooltip("The magnitude of the vector")] [RequiredField]
        public SharedFloat storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = math.length(vector3Variable.Value);
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            vector3Variable = float3.zero;
            storeResult = 0;
        }
    }
}