using System.Text;
using Bonsai.Core;

namespace Bonsai.Standard
{
    /// <summary>
    /// Locks the tree execution for a certain amount of time.
    /// </summary>
    [BonsaiNode("Conditional/", "Condition")]
    public  class Cooldown : ConditionalAbort
    {
        [ShowAtRuntime] public Utility.Timer timer = new Utility.Timer();
        
        /// <summary>
        /// When the timer finishes, automatically unregister from tree update.
        /// </summary>
        public override void OnStart()
        {
            timer.OnTimeout += RemoveTimerFromTreeTick;
        }

        /// <summary>
        /// We can only traverse the child if the cooldown is inactive.
        /// </summary>
        public override void OnEnter()
        {
            if (timer.IsDone)
            {
                Iterator.Traverse(this._child);
            }
        }

        /// <summary>
        /// Only start time if not yet running 
        /// </summary>
        public override void OnExit()
        {
            if (timer.IsDone)
            {
                Tree.AddTimer(timer);
                timer.Start();
            }
        }

        /// <summary>
        /// Abort if the cooldown status changed.
        /// </summary>
        public override bool Condition()
        {
            return timer.IsDone;
        }

        /// <summary>
        /// If the cooldown is active, fail to lock the branch from running, else pass the child branch status.
        /// </summary>
        public override NodeStatus Run()
        {
            if (timer.IsRunning)
            {
                return NodeStatus.Failure;
            }

            return Iterator.LastChildExitStatus.GetValueOrDefault(NodeStatus.Failure);
        }

        private void RemoveTimerFromTreeTick()
        {
            Tree.RemoveTimer(timer);

            // Time is done. Notify abort.
            Evaluate();
        }

        public override void Description(StringBuilder builder)
        {
            base.Description(builder);
            builder.AppendLine();
            builder.AppendFormat("Lock execution for {0:0.00}s", timer.interval);
        }
    }
}