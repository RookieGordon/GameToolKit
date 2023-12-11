using Unity.Mathematics;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityVector2
{
    [TaskCategory("Unity/Vector2")]
    [TaskDescription("Lerp the Vector2 by an amount.")]
    public class Lerp : Action
    {
        [Tooltip("The from value")] public SharedVector2 fromVector2;
        [Tooltip("The to value")] public SharedVector2 toVector2;
        [Tooltip("The amount to lerp")] public SharedFloat lerpAmount;

        [Tooltip("The lerp resut")] [RequiredField]
        public SharedVector2 storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = math.lerp(fromVector2.Value, toVector2.Value, lerpAmount.Value);
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            fromVector2 = float2.zero;
            toVector2 = float2.zero;
            storeResult = float2.zero;
            lerpAmount = 0;
        }
    }
}