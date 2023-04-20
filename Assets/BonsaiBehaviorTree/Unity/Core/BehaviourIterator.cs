namespace Bonsai.Core
{
#if UNITY_EDITOR
    public partial class BehaviourIterator
    {
        private void SetNodeEditorResult(BehaviourNode node, BehaviourNode.StatusEditor result)
        {
            if (node.Proxy != null)
            {
                node.Proxy.StatusEditorResult = result;
            }
        }
    }
#endif
}