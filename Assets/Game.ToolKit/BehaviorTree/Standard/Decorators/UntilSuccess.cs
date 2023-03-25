using Bonsai.Core;

namespace Bonsai.Standard
{
    /// <summary>
    /// Keep re-traversing children until the child return success.
    /// </summary>
    [BonsaiNode("Decorators/", "RepeatArrow")]
    public class UntilSuccess : Decorator
    {
        public override NodeStatus Run()
        {
            NodeStatus s = Iterator.LastChildExitStatus.GetValueOrDefault(NodeStatus.Success);

            if (s == NodeStatus.Success)
            {
                return NodeStatus.Success;
            }

            // Retraverse child.
            Iterator.Traverse(_child);

            return NodeStatus.Running;
        }
    }
}