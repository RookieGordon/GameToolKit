using System.Text;
using Bonsai.Core;

namespace Bonsai.Standard
{
    [BonsaiNode("Tasks/", "TreeIcon")]
    public class Include : Task
    {
#if UNITY_EDITOR
        [UnityEngine.Tooltip("The sub-tree to run when this task executes.")]
#endif
        public BehaviourTree subtreeAsset;
        
        public BehaviourTree RunningSubTree { get; private set; }

        public override void OnStart()
        {
            if (subtreeAsset != null)
            {
                RunningSubTree = BehaviourTree.Clone(subtreeAsset);
                RunningSubTree.actor = Actor;
                RunningSubTree.Start();
            }
        }

        public override void OnEnter()
        {
            RunningSubTree.BeginTraversal();
        }

        public override void OnExit()
        {
            if (RunningSubTree.IsRunning())
            {
                RunningSubTree.Interrupt();
            }
        }

        public override NodeStatus Run()
        {
            if (RunningSubTree != null)
            {
                RunningSubTree.UpdateTree();
                return RunningSubTree.IsRunning()
                    ? NodeStatus.Running
                    : RunningSubTree.LastStatus();
            }

            // No tree was included. Just fail.
            return NodeStatus.Failure;
        }

        public override void Description(StringBuilder builder)
        {
            if (subtreeAsset != null)
            {
                builder.AppendFormat("Include {0}", subtreeAsset.name);
            }
            else
            {
                builder.Append("Tree not set");
            }
        }
    }
}