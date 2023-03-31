using System.Collections.Generic;
using System.Linq;
using System.Text;
using Bonsai.Core;


namespace Bonsai.Standard
{
    [BonsaiNode("Decorators/", "Shield")]
    public partial class Guard : Decorator
    {
        public int maxActiveGuards = 1;

        private int runningGuards = 0;
        private bool childRan = false;

        public override void OnEnter()
        {
            // Do not run the child immediately.
        }

        public override NodeStatus Run()
        {
            // If we enter the run state of the guard, that means
            // the child already returned.
            if (childRan)
            {
                return Iterator.LastChildExitStatus.GetValueOrDefault(NodeStatus.Failure);
            }

            bool bGuardsAvailable = IsRunningGuardsAvailable();

            // Cannot wait for the child, return.
            if (!waitUntilChildAvailable && !bGuardsAvailable)
            {
                return returnSuccessOnSkip ? NodeStatus.Success : NodeStatus.Failure;
            }

            else if (!childRan && bGuardsAvailable)
            {
                // Notify the other guards that this guard runned its child.
                for (int i = 0; i < linkedGuards.Count; ++i)
                {
                    linkedGuards[i].runningGuards += 1;
                }

                childRan = true;
                Iterator.Traverse(Child);
            }

            // Wait for child.
            return NodeStatus.Running;
        }

        // Makes sure that the running guards does not exceed that max capacity.
        private bool IsRunningGuardsAvailable()
        {
            return runningGuards < maxActiveGuards;
        }

        public override void OnExit()
        {
            if (childRan)
            {
                runningGuards -= 1;

                // Notify the rest of the guards that this guard finished.
                for (int i = 0; i < linkedGuards.Count; ++i)
                {
                    linkedGuards[i].runningGuards -= 1;
                }
            }

            childRan = false;
        }

        public override void OnCopy()
        {
            // Only get the instance version of guards under the tree root.
            linkedGuards = linkedGuards
                .Where(i => i.PreOrderIndex != KInvalidOrder)
                .Select(i => BehaviourTree.GetInstanceVersion<Guard>(Tree, i))
                .ToList();
        }

        public override BehaviourNode[] GetReferencedNodes()
        {
            return linkedGuards.ToArray();
        }

        public override void Description(StringBuilder builder)
        {
            builder.AppendFormat("Guarding {0}", linkedGuards.Count);
            builder.AppendLine();
            builder.AppendFormat("Active allowed: {0}", maxActiveGuards);
            builder.AppendLine();
            builder.AppendLine(waitUntilChildAvailable ? "Wait for child branch" : "Skip child branch");
            builder.Append(returnSuccessOnSkip ? "Succeed on skip" : "Fail on skip");
        }
    }
}