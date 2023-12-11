using Unity.Mathematics;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityVector2
{
    [TaskCategory("Unity/Vector2")]
    [TaskDescription("Stores the dot product of two Vector2 values.")]
    public class Dot : Action
    {
        [Tooltip("The left hand side of the dot product")]
        public SharedVector2 leftHandSide;

        [Tooltip("The right hand side of the dot product")]
        public SharedVector2 rightHandSide;

        [Tooltip("The dot product result")] [RequiredField]
        public SharedFloat storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = math.dot(leftHandSide.Value, rightHandSide.Value);
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            leftHandSide = float2.zero;
            rightHandSide = float2.zero;
            storeResult = 0;
        }
    }
}