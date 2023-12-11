using Unity.Mathematics;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityVector3
{
    [TaskCategory("Unity/Vector3")]
    [TaskDescription("Stores the Vector2 value of the Vector3.")]
    public class GetVector2 : Action
    {
        [Tooltip("The Vector3 to get the Vector2 value of")]
        public SharedVector3 vector3Variable;

        [Tooltip("The Vector2 value")] [RequiredField]
        public SharedVector2 storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = vector3Variable.Value.Tofloat2();
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            vector3Variable = float3.zero;
            storeResult = float2.zero;
        }
    }
}