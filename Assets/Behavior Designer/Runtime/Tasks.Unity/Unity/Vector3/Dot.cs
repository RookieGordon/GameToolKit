using Unity.Mathematics;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityVector3
{
    [TaskCategory("Unity/Vector3")]
    [TaskDescription("Stores the dot product of two Vector3 values.")]
    public class Dot : Action
    {
        [Tooltip("The left hand side of the dot product")]
        public SharedVector3 leftHandSide;

        [Tooltip("The right hand side of the dot product")]
        public SharedVector3 rightHandSide;

        [Tooltip("The dot product result")] [RequiredField]
        public SharedFloat storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = math.dot(leftHandSide.Value, rightHandSide.Value);
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            leftHandSide = float3.zero;
            rightHandSide = float3.zero;
            storeResult = 0;
        }
    }
}