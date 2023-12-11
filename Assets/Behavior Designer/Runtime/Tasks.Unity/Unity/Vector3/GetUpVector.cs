using Unity.Mathematics;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityVector3
{
    [TaskCategory("Unity/Vector3")]
    [TaskDescription("Stores the up vector value.")]
    public class GetUpVector : Action
    {
        [Tooltip("The stored result")] [RequiredField]
        public SharedVector3 storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = math.up();
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            storeResult = float3.zero;
        }
    }
}