using System.Runtime.Serialization;

namespace Bonsai.Core
{
    /// <summary>
    /// The base class for all decorators. 
    /// </summary>
    public abstract class Decorator : BehaviourNode
    {
        [Newtonsoft.Json.JsonProperty] protected BehaviourNode _child;

        /// <summary>
        /// Gets the child.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public BehaviourNode Child => this._child;

        /// <summary>
        /// Default behaviour is to immediately try to traverse its child.
        /// </summary>
        public override void OnEnter()
        {
            if (this._child != null)
            {
                Iterator.Traverse(this._child);
            }
        }

        /// <summary>
        /// <para>Set the child for the decorator node.</para>
        /// <para>
        /// This should be called <b>once</b> when the tree is being built,
        /// before Tree Start() and never during Tree Update()
        /// </para>
        /// </summary>
        public void SetChild(BehaviourNode node)
        {
            this._child = node;
            if (this._child != null)
            {
                this._child.Parent = this;
                this._child.indexOrder = 0;
            }
        }

        public sealed override void OnAbort(int childIndex)
        {
        }

        public override void OnCompositeParentExit()
        {
            // Propogate composite parent exit through decorator chain only. No need to call for composite children since composite nodes handle that.
            if (this._child != null && this._child.IsDecorator())
            {
                this._child.OnCompositeParentExit();
            }
        }

        public sealed override int MaxChildCount()
        {
            return 1;
        }

        public sealed override int ChildCount()
        {
            return this._child != null ? 1 : 0;
        }

        public sealed override BehaviourNode GetChildAt(int index)
        {
            return this._child;
        }

        [OnDeserialized]
        public void OnDeserialized(StreamingContext context)
        {
            if (this._child != null && this._child.Parent == null)
            {
                this._child.Parent = this;
            }
        }
    }
}