﻿using System;
using System.Collections.Generic;
using Bonsai.Utility;

namespace Bonsai.Core
{
    /// <summary>
    /// A special iterator to handle traversing a behaviour tree.
    /// </summary>
    public partial class BehaviourIterator
    {
        /// <summary>
        /// Keeps track of the traversal path. Useful to help on aborts and interrupts.
        /// </summary>
        private readonly Utility.FixedSizeStack<int> _traversalStack;

        /// <summary>
        /// Access to the tree so we can find any node from pre-order index.
        /// </summary>
        private readonly BehaviourTree _tree;

        /// <summary>
        /// Keeps track of the traversal path. Before performing the first node, all the nodes you have traveled need to enter first.
        /// </summary>
        private readonly Queue<int> _requestedTraversals;

        /// <summary>
        /// Called when the iterators finishes iterating the entire tree.
        /// </summary>
        public Action OnIterateDone;

        public bool IsRunning => this._traversalStack.Count != 0 && !this._tree.DebugStopped;

        /// <summary>
        /// Gets the pre-order index of the node at the top of the traversal stack.
        /// If the iterator is not traversing anything, -1 is returned.
        /// </summary>
        public int CurrentIndex =>
            this._traversalStack.Count == 0 ? BehaviourNode.KInvalidOrder : this._traversalStack.Peek();

        public int LevelOffset { get; }

        /// <summary>
        /// The last status returned by an exiting child. Reset when nodes are entered.
        /// </summary>
        public NodeStatus? LastChildExitStatus { get; private set; }

        public NodeStatus LastExecutedStatus { get; private set; }

        /// <summary>
        /// Gets the pre-order index of the node at the beginning of the traversal stack.
        /// </summary>
        public int FirstInTraversal => this._traversalStack.GetValue(0);

        public BehaviourIterator(BehaviourTree tree, int levelOffset)
        {
            this._tree = tree;

            // Since tree heights starts from zero, the stack needs to have treeHeight + 1 slots.
            int maxTraversalLength = this._tree.Height + 1;
            this._traversalStack = new Utility.FixedSizeStack<int>(maxTraversalLength);
            this._requestedTraversals = new Queue<int>(maxTraversalLength);

            LevelOffset = levelOffset;
        }

        /// <summary>
        /// Ticks the iterator.
        /// </summary>
        public void Update()
        {
            CallOnEnterOnQueuedNodes();
            int index = this._traversalStack.Peek();
            BehaviourNode node = this._tree.Nodes[index];
            NodeStatus s = node.Run();

            LastExecutedStatus = s;
            
            SetNodeEditorResult(node, (BehaviourNode.StatusEditor)s);

            if (s != NodeStatus.Running)
            {
                PopNode();
                OnChildExit(node, s);
            }

            if (this._traversalStack.Count == 0)
            {
                OnIterateDone?.Invoke();
            }
        }

        /// <summary>
        /// Enter the nodes traversed in turn.
        /// </summary>
        private void CallOnEnterOnQueuedNodes()
        {
            // Make sure to call on enter on any queued new traversals.
            while (this._requestedTraversals.Count != 0)
            {
                int i = this._requestedTraversals.Dequeue();
                BehaviourNode node = this._tree.Nodes[i];
                node.OnEnter();
                OnChildEnter(node);
            }
        }

        private void OnChildEnter(BehaviourNode node)
        {
            if (node.Parent == null)
            {
                return;
            }

            LastChildExitStatus = null;
            node.Parent.OnChildEnter(node.indexOrder);
        }

        private void OnChildExit(BehaviourNode node, NodeStatus s)
        {
            if (node.Parent != null)
            {
                node.Parent.OnChildExit(node.indexOrder, s);
                LastChildExitStatus = s;
            }
        }

        /// <summary>
        /// Requests the iterator to traverse a new node.
        /// </summary>
        public void Traverse(BehaviourNode next)
        {
            int index = next.preOrderIndex;
            this._traversalStack.Push(index);
            this._requestedTraversals.Enqueue(index);
            this._tree.Debugger?.UpdateDebugIndex(index);
            SetNodeEditorResult(next, BehaviourNode.StatusEditor.Running);
        }

        /// <summary>
        /// Interrupt the trees rooted in the current node, and re -execute from the specified child node.
        /// </summary>
        /// <param name="parent">root node.</param>
        /// <param name="abortBranchIndex">The child branch that caused the abort.</param>
        public void AbortRunningChildBranch(BehaviourNode parent, int abortBranchIndex)
        {
            // If the iterator is inactive, ignore.
            if (IsRunning && parent != null)
            {
                int terminatingIndex = parent.preOrderIndex;

                // Exit all nodes in turn until the parent node of the branch that needs to be interrupted
                while (this._traversalStack.Count != 0 && this._traversalStack.Peek() != terminatingIndex)
                {
                    StepBackAbort();
                }

                //QUESTION Why only composite nodes need to worry about which of their subtrees fired an abort.
                if (parent.IsComposite())
                {
                    parent.OnAbort(abortBranchIndex);
                }

                // Any requested traversals are cancelled on abort.
                this._requestedTraversals.Clear();

                // Start with the branch that needs to be interrupted again.
                Traverse(parent.GetChildAt(abortBranchIndex));
            }
        }

        /// <summary>
        /// aborts the current node. If the node is a composite, it aborts the entire subtree.
        /// </summary>
        private void StepBackAbort()
        {
            var node = PopNode();
            SetNodeEditorResult(node, BehaviourNode.StatusEditor.Aborted);
        }

        /// <summary>
        /// Interrupts the subtree traversed by the iterator.
        /// </summary>
        internal void Interrupt(BehaviourNode subtree)
        {
            // Keep interrupting up to the parent of subtree. 
            // The parent is not interrupted; subtree node is interrupted.
            if (subtree != null)
            {
                int parentIndex = subtree.Parent?.PreOrderIndex ?? BehaviourNode.KInvalidOrder;
                while (this._traversalStack.Count != 0 && this._traversalStack.Peek() != parentIndex)
                {
                    var node = PopNode();
                    SetNodeEditorResult(node, BehaviourNode.StatusEditor.Interruption);
                }

                // Any requested traversals are cancelled on interruption.
                this._requestedTraversals.Clear();
            }
        }

        /// <summary>
        /// Rewind from previous traversal records
        /// </summary>
        private BehaviourNode PopNode()
        {
            int index = this._traversalStack.Pop();
            BehaviourNode node = this._tree.Nodes[index];

            if (node.IsComposite())
            {
                for (int i = 0; i < node.ChildCount(); i++)
                {
                    node.GetChildAt(i).OnCompositeParentExit();
                }
            }

            node.OnExit();
            return node;
        }

#if !UNITY_EDITOR
        private void SetNodeEditorResult(BehaviourNode node, BehaviourNode.StatusEditor result)
        {
            
        }
#endif
    }
}