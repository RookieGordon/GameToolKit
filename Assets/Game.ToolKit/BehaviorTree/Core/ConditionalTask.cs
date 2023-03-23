﻿namespace Bonsai.Core
{
    /// <summary>
    /// Base class for all tasks that perform a boolean calculation.
    /// </summary>
    public abstract class ConditionalTask : Task
    {
        /// <summary>
        /// The condition that determines if this task succeeds (true) or fails (false).
        /// </summary>
        /// <returns></returns>
        public abstract bool Condition();

        public override NodeStatus Run()
        {
            bool bResult = Condition();

            return bResult ? NodeStatus.Success : NodeStatus.Failure;
        }
    }
}