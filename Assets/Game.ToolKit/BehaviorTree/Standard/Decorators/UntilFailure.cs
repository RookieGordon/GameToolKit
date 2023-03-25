using Bonsai.Core;

namespace Bonsai.Standard
{
    /// <summary>
    /// Re-traversing the child until it returns failure.
    /// </summary>
    [BonsaiNode("Decorators/", "RepeatArrow")]
    public class UntilFailure : Decorator
    {
        public override NodeStatus Run()
        {
            NodeStatus s = Iterator.LastChildExitStatus.GetValueOrDefault(NodeStatus.Failure);

            if (s == NodeStatus.Failure)
            {
                return NodeStatus.Success;
            }

            // Retraverse child.
            Iterator.Traverse(_child);

            return NodeStatus.Running;
        }
    }
}