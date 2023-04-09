using Bonsai.Core;

namespace Bonsai.Standard
{
    /// <summary>
    /// Parallel node which succeeds if all its children succeed.
    /// </summary>
    [BonsaiNode("Composites/", "Parallel")]
    public class Parallel : ParallelComposite
    {
        public override NodeStatus Run()
        {
            if (IsAnyChildWithStatus(NodeStatus.Failure))
            {
                return NodeStatus.Failure;
            }

            if (AreAllChildrenWithStatus(NodeStatus.Success))
            {
                return NodeStatus.Success;
            }

            RunChildBranches();

            // Parallel iterators still running.
            return NodeStatus.Running;
        }
    }
}