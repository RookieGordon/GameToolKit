using Bonsai.Core;

namespace Bonsai.Standard
{
    [BonsaiNode("Composites/", "ParallelSelector")]
    public class ParallelSelector : Parallel
    {
        public override NodeStatus Run()
        {
            if (IsAnyChildWithStatus(NodeStatus.Success))
            {
                return NodeStatus.Success;
            }

            if (AreAllChildrenWithStatus(NodeStatus.Failure))
            {
                return NodeStatus.Failure;
            }

            // Process the sub-iterators.
            RunChildBranches();

            // Parallel iterators still running.
            return NodeStatus.Running;
        }
    }
}