using Unity.Mathematics;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityVector3
{
    [TaskCategory("Unity/Vector3")]
    [TaskDescription("Lerp the Vector3 by an amount.")]
    public class Lerp : Action
    {
        [Tooltip("The from value")] public SharedVector3 fromVector3;
        [Tooltip("The to value")] public SharedVector3 toVector3;
        [Tooltip("The amount to lerp")] public SharedFloat lerpAmount;

        [Tooltip("The lerp resut")] [RequiredField]
        public SharedVector3 storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = math.lerp(fromVector3.Value, toVector3.Value, lerpAmount.Value);
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            fromVector3 = toVector3 = storeResult = float3.zero;
            lerpAmount = 0;
        }
    }
}