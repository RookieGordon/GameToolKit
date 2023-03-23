using Bonsai.Core;

namespace Bonsai.Standard
{
    /// <summary>
    /// Returns success if one child returns success.
    /// </summary>
    [BonsaiNode("Composites/", "Question")]
    public class Selector : Composite
    {
        public override NodeStatus Run()
        {
            // If a child succeeded then the selector succeeds.
            if (lastChildExitStatus == NodeStatus.Success)
            {
                return NodeStatus.Success;
            }

            // Else child returned failure.

            // Get the next child
            var nextChild = CurrentChild();

            // If this was the last child then the selector fails.
            if (nextChild == null)
            {
                return NodeStatus.Failure;
            }

            // Still need children to process.
            else
            {
                Iterator.Traverse(nextChild);
                return NodeStatus.Running;
            }
        }
    }
}