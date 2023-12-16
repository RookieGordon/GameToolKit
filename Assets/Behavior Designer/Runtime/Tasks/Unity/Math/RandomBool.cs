using System;

namespace BehaviorDesigner.Runtime.Tasks.Unity.Math
{
    [TaskCategory("Unity/Math")]
    [TaskDescription("Sets a random bool value")]
    public class RandomBool : Action
    {
        [Tooltip("The variable to store the result")]
        public SharedBool storeResult;

        public Random random = new Random();

        public override TaskStatus OnUpdate()
        {
            storeResult.Value = random.RandomRange(0, 1) < 0.5f;
            return TaskStatus.Success;
        }
    }
}