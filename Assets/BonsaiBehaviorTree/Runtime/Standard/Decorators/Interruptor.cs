using System.Collections.Generic;
using System.Linq;
using Bonsai.Core;


namespace Bonsai.Standard
{
    [BonsaiNode("Tasks/", "Interruptor")]
    public class Interruptor : Task
    {
        public List<Interruptable> linkedInterruptables = new List<Interruptable>();
        
#if UNITY_EDITOR
        [UnityEngine.Tooltip("If true, then the interruptable node return success else failure.")]
#endif        
        public bool returnSuccess = false;

        public override NodeStatus Run()
        {
            for (int i = 0; i < linkedInterruptables.Count; ++i)
            {
                NodeStatus interruptionStatus = returnSuccess ? NodeStatus.Success : NodeStatus.Failure;
                linkedInterruptables[i].PerformInterruption(interruptionStatus);
            }

            return NodeStatus.Success;
        }

        public override void OnCopy()
        {
            // Only get the instance version of interruptables under the tree root.
            linkedInterruptables = linkedInterruptables
                .Where(i => i.PreOrderIndex != KInvalidOrder)
                .Select(i => BehaviourTree.GetInstanceVersion<Interruptable>(Tree, i))
                .ToList();
        }

        public override BehaviourNode[] GetReferencedNodes()
        {
            return linkedInterruptables.ToArray();
        }
    }
}