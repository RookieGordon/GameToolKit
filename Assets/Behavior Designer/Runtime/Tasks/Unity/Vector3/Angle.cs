﻿using Unity.Mathematics;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityVector3
{
    [TaskCategory("Unity/Vector3")]
    [TaskDescription("Returns the angle between two Vector3s.")]
    public class Angle : Action
    {
        [Tooltip("The first Vector3")] public SharedVector3 firstVector3;
        [Tooltip("The second Vector3")] public SharedVector3 secondVector3;
        [Tooltip("The angle")] [RequiredField] public SharedFloat storeResult;

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = MathUtils.VectorAngle(firstVector3.Value, secondVector3.Value);
            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            firstVector3 = float3.zero;
            secondVector3 = float3.zero;
            storeResult = 0;
        }
    }
}