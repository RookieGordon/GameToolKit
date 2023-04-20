namespace Bonsai.Core
{
#if UNITY_EDITOR
    public partial class BehaviourNode
    {
        [Newtonsoft.Json.JsonIgnore] public BehaviourNodeProxy Proxy;
        
        /// <summary>
        /// The game object associated with the tree of this node.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore] protected UnityEngine.GameObject Actor => this.treeOwner.actor;
    }
#endif
}
