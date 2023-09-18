using System.Text;

namespace Bonsai.Core
{
    /// <summary>
    /// The return status of a node execution.
    /// </summary>
    public enum NodeStatus
    {
        Success,
        Failure,
        Running
    };

    /// <summary>
    /// The base class for all behaviour nodes.
    /// </summary>
    public abstract partial class BehaviourNode : IIterableNode<BehaviourNode>
    {
        public const int KInvalidOrder = -1;

        internal BehaviourTree treeOwner = null;

        /// <summary>
        /// The order of the node in preorder traversal.
        /// </summary>
        [Newtonsoft.Json.JsonProperty] internal int preOrderIndex = 0;

        /// <summary>
        /// The order of the node in post-order traversal.
        /// </summary>
        internal int postOrderIndex = 0;

        /// <summary>
        /// The order of the node in depth.
        /// </summary>
        internal int levelOrder = 0;

        public BehaviourNode Parent { get; internal set; }
        public BehaviourIterator Iterator { get; internal set; }

        /// <summary>
        /// The order of the node relative to its parent.
        /// </summary>
        protected internal int indexOrder = 0;

        public int AssetInstanceID;

        public string name;

        /// <summary>
        /// The tree that owns the node.
        /// </summary>
        public BehaviourTree Tree => this.treeOwner;

        /// <summary>
        /// The index position of the node under its parent (if any).
        /// </summary>
        public int ChildOrder => this.indexOrder;

        /// <summary>
        /// The order of the node in preorder traversal.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore] public int PreOrderIndex => this.preOrderIndex;

        /// <summary>
        /// The order of the node in post-order traversal.
        /// </summary>
        public int PostOrderIndex => this.postOrderIndex;

        public int LevelOrder => this.levelOrder;

        /// <summary>
        /// Gets the blackboard used by the parent tree.
        /// </summary>
        protected Blackboard Blackboard => this.treeOwner.Blackboard;

        /// <summary>
        /// Called when the tree is started.
        /// </summary>
        public virtual void OnStart()
        {
        }

        /// <summary>
        /// Executes when the node is at the top of the execution.
        /// </summary>
        /// <returns></returns>
        public abstract NodeStatus Run();

        /// <summary>
        /// Called when a traversal begins on the node.
        /// </summary>
        public virtual void OnEnter()
        {
        }

        /// <summary>
        /// Called when a traversal on the node ends.
        /// </summary>
        public virtual void OnExit()
        {
        }

        /// <summary>
        /// Used to evaluate which branch should execute first with the utility selector.
        /// </summary>
        /// <returns></returns>
        public virtual float UtilityValue()
        {
            return 0f;
        }

        /// <summary>
        /// Called when a child fires an abort.
        /// </summary>
        public virtual void OnAbort(int childIndex)
        {
        }

        /// <summary>
        /// Called when the iterator traverses the child.
        /// </summary>
        public virtual void OnChildEnter(int childIndex)
        {
        }

        /// <summary>
        /// Called when the iterator exits the the child.
        /// </summary>
        /// <param name="childIndex"></param>
        /// <param name="childStatus"></param>
        public virtual void OnChildExit(int childIndex, NodeStatus childStatus)
        {
        }

        /// <summary>
        /// Called foreach child of the composite node when it exits.
        /// </summary>
        public virtual void OnCompositeParentExit()
        {
        }

        /// <summary>
        /// Called when after the entire tree is finished being copied.
        /// Should be used to setup special BehaviourNode references.
        /// </summary>
        public virtual void OnCopy()
        {
        }

        /// <summary>
        /// A helper method to return nodes that being referenced.
        /// </summary>
        /// <returns></returns>
        public virtual BehaviourNode[] GetReferencedNodes()
        {
            return null;
        }

        public abstract BehaviourNode GetChildAt(int index);
        public abstract int ChildCount();
        public abstract int MaxChildCount();

        /// <summary>
        /// Returns true if the node is a composite node, the number of children node more then 1.
        /// </summary>
        public bool IsComposite()
        {
            return MaxChildCount() > 1;
        }

        public bool IsDecorator()
        {
            return MaxChildCount() == 1;
        }

        public bool IsTask()
        {
            return MaxChildCount() == 0;
        }

        /// <summary>
        /// A summary description of the node.
        /// </summary>
        public virtual void Description(StringBuilder builder)
        {
            // Default adds no description
        }

        #region Node Editor Meta Data
        /// <summary>
        /// Statuses used by the editor to know how to visually represent the node.
        /// It is the same as the Status enum but has extra enums useful to the editor.
        /// </summary>
        public enum StatusEditor
        {
            Success,
            Failure,
            Running,
            None,
            Aborted,
            Interruption
        };
        #endregion
    }
}