using System;

namespace BehaviorDesigner.Runtime.Tasks
{
    [TaskDescription("Waits the specified duration after the child has completed before returning the child's status of success or failure.")]
    [TaskIcon("{SkinColor}CooldownIcon.png")]
    public class Cooldown : Decorator
    {
        public SharedFloat duration = 2;

        // The status of the child after it has finished running.
        private TaskStatus executionStatus = TaskStatus.Inactive;
        private DateTime cooldownTime = default;

        public override bool CanExecute()
        {
            if (cooldownTime == default)
            {
                return true;
            }

            return cooldownTime.AddSeconds(duration.Value) > DateTime.UtcNow;
        }

        public override int CurrentChildIndex()
        {
            if (cooldownTime == default)
            {
                return 0;
            }

            return -1;
        }

        public override void OnChildExecuted(TaskStatus childStatus)
        {
            executionStatus = childStatus;
            if (executionStatus == TaskStatus.Failure || executionStatus == TaskStatus.Success)
            {
                cooldownTime = DateTime.UtcNow;
            }
        }

        public override TaskStatus OverrideStatus()
        {
            if (!CanExecute())
            {
                return TaskStatus.Running;
            }

            return executionStatus;
        }

        public override TaskStatus OverrideStatus(TaskStatus status)
        {
            if (status == TaskStatus.Running)
            {
                return status;
            }

            return executionStatus;
        }

        public override void OnEnd()
        {
            // Reset the execution status back to its starting values.
            executionStatus = TaskStatus.Inactive;
            cooldownTime = default;
        }
    }
}