using Unity.Mathematics;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityVector2
{
    [TaskCategory("Unity/Vector2")]
    [TaskDescription("Stores the up vector value.")]
    public class GetUpVector : Action
    {
        [Tooltip("The stored result")] [RequiredField]
        public SharedVector2 storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = math.up().Tofloat2();
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            storeResult = float2.zero;
        }
    }
}