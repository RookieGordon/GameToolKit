using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Bonsai.Utility;

namespace Bonsai.Core
{
    public class TreeDebugger
    {
        public int BreakPoint = -1;
        public int DebugIndex = -1;
        public bool Running = false;
        public bool StepOver = false;
        public bool Resume = false;

        /// <summary>
        /// stop tree before every update
        /// </summary>
        public bool IsStop
        {
            get
            {
                if (!Running)
                {
                    return false;
                }

                if (Resume)
                {
                    return BreakPoint == DebugIndex;
                }

                if (StepOver)
                {
                    return false;
                }

                return BreakPoint <= DebugIndex;
            }
        }

        public void Update()
        {
            if (!Running)
            {
                return;
            }

            if (Resume && BreakPoint == DebugIndex)
            {
                Resume = false;
            }

            StepOver = false;
        }

        public void StopDebug()
        {
            Running = false;
            BreakPoint = -1;
            DebugIndex = -1;
        }

        public void UpdateDebugIndex(int index)
        {
            Log.LogInfo($"UpdateDebugIndex {index}");
            DebugIndex = index;
        }
    }

    public partial class BehaviourTree
    {
        /// <summary>
        /// The iterator that ticks branches under the tree root. Does not tick branches under parallel nodes since those use their own parallel iterators.
        /// </summary>
        [Newtonsoft.Json.JsonProperty] private BehaviourIterator _mainIterator;

        /// <summary>
        /// Active timers that tick while the tree runs.
        /// </summary>
        [Newtonsoft.Json.JsonProperty] private Utility.UpdateList<Utility.Timer> _activatedTimers;

        /// <summary>
        /// Flags if the tree has been initialized and is ready to run.
        /// <para>This is set on tree Start. If false, the tree will not be traversed for running.</para>>
        /// </summary>
        private bool _isTreeInitialized = false;


        /// <summary>
        /// allNodes must always be kept in pre-order.
        /// </summary>
#pragma warning disable IDE0044 // Add readonly modifier
        [Newtonsoft.Json.JsonProperty] private BehaviourNode[] allNodes = { };
#pragma warning restore IDE0044 // Add readonly modifier

        public string name;

        public int AssetInstanceID;

        /// <summary>
        /// The nodes in the tree in pre-order.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public BehaviourNode[] Nodes => allNodes;

        public Blackboard Blackboard;

        /// <summary>
        /// The first node in the tree. Also the entry point to run the tree.
        /// </summary>
        public BehaviourNode Root => allNodes.Length == 0 ? null : allNodes[0];

        /// <summary>
        /// The maximum height of the tree. This is the height measured from the root to the furthest leaf.
        /// </summary>
        public int Height { get; private set; } = 0;

        [Newtonsoft.Json.JsonIgnore] public int ActiveTimerCount => this._activatedTimers.Data.Count;

        [Newtonsoft.Json.JsonIgnore] public TreeDebugger Debugger;

        [Newtonsoft.Json.JsonIgnore] public static Action<BehaviourTree> AfterInit;

        public bool DebugStopped => Debugger != null && Debugger.IsStop;

        /// <summary>
        /// <para>Preprocesses and starts the tree. This can be thought of as the tree initializer. </para>
        /// Does not begin the tree traversal.
        /// <see cref="BeginTraversal"/>
        /// </summary>
        public void Start()
        {
            if (Root == null)
            {
                Log.LogWarning("Cannot start tree with a null root.");
                return;
            }

            this.PreProcessInternal();

            foreach (BehaviourNode node in allNodes)
            {
                node.OnStart();
            }

            this._isTreeInitialized = true;
        }

        /// <summary>
        /// Ticks (steps) the tree once.
        /// <para>The tree must be started beforehand.</para>
        /// <see cref="Start"/>
        /// </summary>
        public void UpdateTree(float deltaTime = 0f)
        {
            if (this._isTreeInitialized && this._mainIterator.IsRunning && !DebugStopped)
            {
                this.UpdateTimers(deltaTime);
                this._mainIterator.Update();
                Debugger?.Update();
            }
        }

        /// <summary>
        /// <para>Start traversing the tree from the root. Can only be done if the tree is not yet running. </para>
        /// The tree should be initialized before calling this.
        /// <see cref="Start"/>
        /// </summary>
        public void BeginTraversal()
        {
            if (this._isTreeInitialized && !this._mainIterator.IsRunning)
            {
                this._mainIterator.Traverse(Root);
            }
        }

        /// <summary>
        /// Sets the tree nodes. Must be in pre-order.
        /// </summary>
        /// <param name="node">The nodes in pre-order.</param>
        public void SetNodes(IEnumerable<BehaviourNode> nodes)
        {
            allNodes = nodes.ToArray();
            int preOrderIndex = 0;
            foreach (BehaviourNode node in allNodes)
            {
                node.treeOwner = this;
                node.preOrderIndex = preOrderIndex++;
                this.SetNodeProxyIndex(node);
            }
        }
        
#if !UNITY_EDITOR
        private void SetNodeProxyIndex(BehaviourNode node)
        {
        }
#endif

        /// <summary>
        /// Traverses the nodes under the root in pre-order and sets the tree nodes.
        /// </summary>
        /// <param name="root">The tree root</param>
        public void SetNodes(BehaviourNode root)
        {
            SetNodes(TraversalHelper.PreOrder(root));
        }

        /// <summary>
        /// Gets the node at the specified pre-order index.
        /// </summary>
        public BehaviourNode GetNode(int preOrderIndex)
        {
            return allNodes[preOrderIndex];
        }

        /// <summary>
        /// Interrupts the branch from the subroot.
        /// </summary>
        /// <param name="subroot">The node where the interruption will begin from.</param>
        public static void Interrupt(BehaviourNode subroot)
        {
            // Interrupt this subtree.
            subroot.Iterator.Interrupt(subroot);
        }

        /// <summary>
        /// Interrupts the entire tree.
        /// </summary>
        public void Interrupt()
        {
            Interrupt(Root);
        }

        /// <summary>
        /// Add the timer so it gets ticked whenever the tree ticks.
        /// </summary>
        public void AddTimer(Utility.Timer timer)
        {
            this._activatedTimers.Add(timer);
        }

        /// <summary>
        /// Remove the timer from being ticked by the tree.
        /// </summary>
        public void RemoveTimer(Utility.Timer timer)
        {
            this._activatedTimers.Remove(timer);
        }

        private void UpdateTimers(float deltaTime = 0f)
        {
            var timers = this._activatedTimers.Data;
            var count = this._activatedTimers.Data.Count;
            for (int i = 0; i < count; i++)
            {
                timers[i].Update(deltaTime);
            }

            this._activatedTimers.AddAndRemoveQueued();
        }

        /// <summary>
        /// Gets the nodes of type T.
        /// </summary>
        public IEnumerable<T> GetNodes<T>() where T : BehaviourNode
        {
            return allNodes.Select(node => node as T).Where(casted => casted != null);
        }

        /// <summary>
        /// Helper method to pre-process the tree before calling Start on nodes.
        /// <para>Mainly does caching and sets node index orders.</para>>
        /// </summary>
        private void PreProcessInternal()
        {
            this.SetPostandLevelOrders();

            this._mainIterator = new BehaviourIterator(this, 0);
            this._activatedTimers = new Utility.UpdateList<Utility.Timer>();

            this.SetRootIteratorReferences();
        }

        /// <summary>
        /// Assign the main iterator to nodes not under any parallel nodes.
        /// <para> Children under parallel nodes will have iterators assigned by the parallel parent. Each branch under a parallel node use their own branch iterator. </para>
        /// </summary>
        private void SetRootIteratorReferences()
        {
            foreach (BehaviourNode node in TraversalHelper.PreOrderSkipChildren(Root, n => n is ParallelComposite))
            {
                node.Iterator = this._mainIterator;
            }
        }

        /// <summary>
        /// Sets the nodes post and level order numbering.
        /// </summary>
        private void SetPostandLevelOrders()
        {
            int postOrderIndex = 0;
            foreach (BehaviourNode node in TraversalHelper.PostOrder(Root))
            {
                node.postOrderIndex = postOrderIndex++;
            }

            var enumerableList = TraversalHelper.LevelOrder(Root);
            foreach ((BehaviourNode node, int level) in enumerableList)
            {
                node.levelOrder = level;
                Height = level;
            }
        }

        /// <summary>
        /// Tests if the order of a is lower than b.
        /// <para>1 is the highest priority. Greater numbers means lower priority.</para>
        /// </summary>
        public static bool IsLowerOrder(int orderA, int orderB)
        {
            return orderA > orderB;
        }

        /// <summary>
        /// Tests if the order of a is higher than b.
        /// </summary>
        public static bool IsHigherOrder(int orderA, int orderB)
        {
            return orderA < orderB;
        }

        /// <summary>
        /// Tests if node is under the root tree.
        /// </summary>
        public static bool IsUnderSubtree(BehaviourNode root, BehaviourNode node)
        {
            // Assume that this is the root of the tree root.
            // This would happen when checking IsUnderSubtree(node.parent, other)
            if (root == null)
            {
                return true;
            }

            return root.PostOrderIndex > node.PostOrderIndex && root.PreOrderIndex < node.PreOrderIndex;
        }

        public bool IsRunning()
        {
            return this._mainIterator is { IsRunning: true };
        }

        public bool IsInitialized()
        {
            return this._isTreeInitialized;
        }

        public NodeStatus LastStatus()
        {
            return this._mainIterator.LastExecutedStatus;
        }

        /// <summary>
        /// Gets the instantiated copy version of a behaviour node from its original version.
        /// </summary>
        /// <param name="tree">The instantiated tree.</param>
        /// <param name="original">The node in the original tree.</param>
        public static T GetInstanceVersion<T>(BehaviourTree tree, BehaviourNode original) where T : BehaviourNode
        {
            return GetInstanceVersion(tree, original) as T;
        }

        public static BehaviourNode GetInstanceVersion(BehaviourTree tree, BehaviourNode original)
        {
            int index = original.preOrderIndex;
            return tree.allNodes[index];
        }

        /// <summary>
        /// Deep copies the tree.
        /// </summary>
        /// <param name="sourceTree">The source tree to clone.</param>
        /// <returns>The cloned tree.</returns>
        public static BehaviourTree Clone(BehaviourTree sourceTree)
        {
            // The tree clone will be blank to start. We will duplicate blackboard and nodes.
            // var cloneBt = CreateInstance<BehaviourTree>();
            var cloneBt = System.Activator.CreateInstance<BehaviourTree>();
            cloneBt.name = sourceTree.name;

            if (sourceTree.Blackboard != null)
            {
                // cloneBt.Blackboard = Instantiate(sourceTree.Blackboard);
                cloneBt.Blackboard = TreeSerializeHelper.CopyObject<Blackboard>(sourceTree.Blackboard);
            }

            // Source tree nodes should already be in pre-order.
            // cloneBt.SetNodes(sourceTree.Nodes.Select(n => Instantiate(n)));
            cloneBt.SetNodes(sourceTree.Nodes.Select(n => TreeSerializeHelper.CopyObject<BehaviourNode>(n)));

            // Relink children and parents for the cloned nodes.
            int maxCloneNodeCount = cloneBt.allNodes.Length;
            for (int i = 0; i < maxCloneNodeCount; ++i)
            {
                BehaviourNode nodeSource = sourceTree.allNodes[i];
                BehaviourNode copyNode = GetInstanceVersion(cloneBt, nodeSource);

                if (copyNode.IsComposite())
                {
                    var copyComposite = copyNode as Composite;
                    Debug.Assert(copyComposite != null, nameof(copyComposite) + " != null");
                    copyComposite.SetChildren(
                        Enumerable.Range(0, nodeSource.ChildCount())
                            .Select(childIndex => GetInstanceVersion(cloneBt, nodeSource.GetChildAt(childIndex)))
                            .ToArray());
                }

                else if (copyNode.IsDecorator() && nodeSource.ChildCount() == 1)
                {
                    var copyDecorator = copyNode as Decorator;
                    Debug.Assert(copyDecorator != null, nameof(copyDecorator) + " != null");
                    var copyChild = GetInstanceVersion(cloneBt, nodeSource.GetChildAt(0));
                    copyDecorator.SetChild(copyChild);
                }
            }

            foreach (BehaviourNode node in cloneBt.allNodes)
            {
                node.OnCopy();
            }

            return cloneBt;
        }

        /// <summary>
        /// Clear tree structure references.
        /// <list type="bullet">
        /// <item>Root</item>
        /// <item>References to parent Tree</item>
        /// <item>Parent-Child connections</item>
        /// <item>Internal Nodes List</item>
        /// </list>
        /// </summary>
        public void ClearStructure()
        {
            foreach (BehaviourNode node in allNodes)
            {
                ClearChildrenStructure(node);
                node.preOrderIndex = BehaviourNode.KInvalidOrder;
                node.indexOrder = 0;
                node.Parent = null;
                node.treeOwner = null;
            }

            allNodes = new BehaviourNode[] { };
        }

        private void ClearChildrenStructure(BehaviourNode node)
        {
            if (node.IsComposite())
            {
                var composite = node as Composite;
                Debug.Assert(composite != null, nameof(composite) + " != null");
                composite.SetChildren(new BehaviourNode[] { });
            }

            else if (node.IsDecorator())
            {
                var decorator = node as Decorator;
                Debug.Assert(decorator != null, nameof(decorator) + " != null");
                decorator.SetChild(null);
            }
        }
    }
}