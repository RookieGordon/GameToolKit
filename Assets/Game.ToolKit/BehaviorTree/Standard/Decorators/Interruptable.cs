using Bonsai.Core;

namespace Bonsai.Standard
{
    [BonsaiNode("Decorators/", "Interruptable")]
    public class Interruptable : Decorator
    {
        private bool isRunning = false;
        private NodeStatus returnStatus = NodeStatus.Failure;
        private bool isInterrupted = false;

        public override void OnEnter()
        {
            isRunning = true;
            isInterrupted = false;
            base.OnEnter();
        }

        public override NodeStatus Run()
        {
            if (isInterrupted)
            {
                return returnStatus;
            }

            return Iterator.LastChildExitStatus.GetValueOrDefault(NodeStatus.Failure);
        }

        public override void OnExit()
        {
            isRunning = false;
        }

        public void PerformInterruption(NodeStatus interruptionStatus)
        {
            if (isRunning)
            {
                isInterrupted = true;
                returnStatus = interruptionStatus;
                BehaviourTree.Interrupt(Child);
            }
        }
    }
}