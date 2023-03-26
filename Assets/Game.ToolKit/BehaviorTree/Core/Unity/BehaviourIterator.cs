namespace Bonsai.Core
{
    public partial class BehaviourIterator
    {
        private void UpdateEditorStatus(BehaviourNode node, NodeStatus status)
        {
#if UNITY_EDITOR
            node.StatusEditorResult = (BehaviourNode.StatusEditor)status;
#endif
        }

        private void SetEditorRunningStatus(BehaviourNode node)
        {
#if UNITY_EDITOR
            node.StatusEditorResult = BehaviourNode.StatusEditor.Running;
#endif
        }
        
        private void SetEditorAbortedStatus(BehaviourNode node)
        {
#if UNITY_EDITOR
            node.StatusEditorResult = BehaviourNode.StatusEditor.Aborted;
#endif
        }

        private void SetEditorInterruptionStatus(BehaviourNode node)
        {
#if UNITY_EDITOR
            node.StatusEditorResult = BehaviourNode.StatusEditor.Interruption;
#endif
        }
    }
}