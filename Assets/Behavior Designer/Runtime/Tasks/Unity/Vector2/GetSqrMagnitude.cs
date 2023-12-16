using Unity.Mathematics;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityVector2
{
    [TaskCategory("Unity/Vector2")]
    [TaskDescription("Stores the square magnitude of the Vector2.")]
    public class GetSqrMagnitude : Action
    {
        [Tooltip("The Vector2 to get the square magnitude of")]
        public SharedVector2 vector2Variable;

        [Tooltip("The square magnitude of the vector")] [RequiredField]
        public SharedFloat storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = math.lengthsq(vector2Variable.Value);
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            vector2Variable = float2.zero;
            storeResult = 0;
        }
    }
}