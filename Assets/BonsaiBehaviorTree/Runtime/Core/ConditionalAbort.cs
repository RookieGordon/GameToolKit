using System.Text;

namespace Bonsai.Core
{
    public enum AbortType
    {
        None,
        LowerPriority,
        Self,
        Both
    };

    /// <summary>
    /// A special type of decorator node that has a condition to fire an abort.
    /// <seealso href="https://gwb.tencent.com/community/detail/122853"/>
    /// </summary>
    public abstract class ConditionalAbort : Decorator
    {
        /// <summary>
        /// A property for decorator nodes that allows the flow of the behaviour tree to change to this node if the condition is satisfied.
        /// </summary>
        public AbortType abortType = AbortType.None;

        public bool IsObserving { get; private set; } = false;

        private bool IsActive { get; set; } = false;

        /// <summary>
        /// The condition that needs to be satisfied for the node to run its children or to abort.
        /// </summary>
        public abstract bool Condition();

        /// <summary>
        /// Called when the observer starts. This can be used to subscribe to events.
        /// </summary>
        protected virtual void OnObserverBegin()
        {
        }

        /// <summary>
        /// Called when the observerer stops. This can be used unsubscribe from events.
        /// </summary>
        protected virtual void OnObserverEnd()
        {
        }

        /// <summary>
        /// Only runs the child if the condition is true.
        /// </summary>
        public override void OnEnter()
        {
            IsActive = true;

            // Observer has become relevant in current context.
            if (abortType != AbortType.None)
            {
                if (!IsObserving)
                {
                    IsObserving = true;
                    OnObserverBegin();
                }
            }

            if (Condition())
            {
                base.OnEnter();
            }
        }

        public override void OnExit()
        {
            // Observer no longer relevant in current context.
            if (abortType is AbortType.None or AbortType.Self)
            {
                if (IsObserving)
                {
                    IsObserving = false;
                    OnObserverEnd();
                }
            }

            IsActive = false;
        }

        /// <summary>
        /// When the parent composite exits, all observers in child branches become irrelevant.
        /// </summary>
        public sealed override void OnCompositeParentExit()
        {
            if (IsObserving)
            {
                IsObserving = false;
                OnObserverEnd();
            }

            // Propogate composite parent exit through decorator chain only.
            base.OnCompositeParentExit();
        }

        /// <summary>
        /// Return what the child returns if it ran, else fail.
        /// </summary>
        public override NodeStatus Run()
        {
            return Iterator.LastChildExitStatus.GetValueOrDefault(NodeStatus.Failure);
        }

        /// <summary>
        /// Execute the result according to the condition and break the branch
        /// </summary>
        protected void Evaluate()
        {
            bool conditionResult = Condition();

            if (IsActive && !conditionResult)
            {
                AbortCurrentBranch();
            }
            else if (!IsActive && conditionResult)
            {
                AbortLowerPriorityBranch();
            }
        }

        /// <summary>
        /// Abort the current branch if abort type is set to self or both.
        /// </summary>
        private void AbortCurrentBranch()
        {
            if (abortType is AbortType.Self or AbortType.Both)
            {
                Iterator.AbortRunningChildBranch(Parent, ChildOrder);
            }
        }

        /// <summary>
        /// Abort the lower priority branch if abort type is set to lower priority or both.
        /// </summary>
        private void AbortLowerPriorityBranch()
        {
            if (abortType is AbortType.LowerPriority or AbortType.Both)
            {
                ConditionalAbort.GetCompositeParent(this, out var compositeParent, out var branchIndex);

                if (compositeParent != null && compositeParent.IsComposite())
                {
                    bool isLowerPriority = ((Composite)compositeParent).CurrentChildIndex > branchIndex;
                    if (isLowerPriority)
                    {
                        Iterator.AbortRunningChildBranch(compositeParent, branchIndex);
                    }
                }
            }
        }

        /// <summary>
        /// Go up from the current node to find the first Composite type node
        /// </summary>
        private static void GetCompositeParent(BehaviourNode child, out BehaviourNode compositeParent,
            out int branchIndex)
        {
            compositeParent = child.Parent;
            branchIndex = child.indexOrder;

            while (compositeParent != null && !compositeParent.IsComposite())
            {
                branchIndex = compositeParent.indexOrder;
                compositeParent = compositeParent.Parent;
            }
        }

        public override void Description(StringBuilder builder)
        {
            builder.AppendFormat("Aborts {0}", this.abortType.ToString());
        }
    }
}