using Bonsai.Core;

namespace Bonsai.Standard
{
    /// <summary>
    /// Negates the status of the child.
    /// </summary>
    [BonsaiNode("Decorators/", "Exclamation")]
    public class Inverter : Decorator
    {
        public override NodeStatus Run()
        {
            NodeStatus s = Iterator.LastChildExitStatus.GetValueOrDefault(NodeStatus.Success);
            return s == NodeStatus.Failure ? NodeStatus.Success : NodeStatus.Failure;
        }
    }
}