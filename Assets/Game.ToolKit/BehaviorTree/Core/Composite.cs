

namespace Bonsai.Core
{
    /// <summary>
    /// The base class for all composite nodes.
    /// </summary>
    public abstract partial class Composite : BehaviourNode
    {
        protected NodeStatus LastChildExitStatus;
        public int CurrentChildIndex { get; private set; } = 0;
        
        public BehaviourNode[] Children => this._children;

        public virtual BehaviourNode CurrentChild()
        {
            if (CurrentChildIndex < this._children.Length)
            {
                return this._children[CurrentChildIndex];
            }

            return null;
        }

        /// <summary>
        /// Default behaviour is to immediately traverse the first child.
        /// </summary>
        public override void OnEnter()
        {
            CurrentChildIndex = 0;
            var next = CurrentChild();
            if (next)
            {
                Iterator.Traverse(next);
            }
        }

        public sealed override int ChildCount()
        {
            return this._children.Length;
        }

        public sealed override BehaviourNode GetChildAt(int index)
        {
            return this._children[index];
        }

        /// <summary>
        /// <para>Set the children for the composite node.</para>
        /// <para>This should be called when the tree is being built.</para>
        /// <para>It should be called before Tree Start() and never during Tree Update()</para>
        /// <note>To clear children references, pass an empty array.</note>
        /// </summary>
        /// <param name="nodes">The children for the node. Should not be null.</param>
        public void SetChildren(BehaviourNode[] nodes)
        {
            this._children = nodes;
            // Set index orders.
            for (int i = 0; i < this._children.Length; i++)
            {
                this._children[i].indexOrder = i;
            }

            // Set parent references.
            foreach (BehaviourNode child in this._children)
            {
                child.Parent = this;
            }
        }

        /// <summary>
        /// Called when a composite node has a child that activates when it aborts.
        /// </summary>
        /// <param name="child"></param>
        public override void OnAbort(int childIndex)
        {
            // The default behaviour is to set the current child index of the composite node.
            CurrentChildIndex = childIndex;
        }

        /// <summary>
        /// Default behaviour is sequential traversal from first to last.
        /// </summary>
        /// <returns></returns>
        public override void OnChildExit(int childIndex, NodeStatus childStatus)
        {
            CurrentChildIndex++;
            LastChildExitStatus = childStatus;
        }

        public sealed override int MaxChildCount()
        {
            return int.MaxValue;
        }
    }
}