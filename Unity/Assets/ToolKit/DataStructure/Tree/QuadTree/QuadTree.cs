/*
 * author       : Gordon
 * datetime     : 2025/3/10
 * description  : 四叉树，参考：https://pvigier.github.io/2019/08/04/quadtree-collision-detection.html
 */

using System;
using System.Collections.Generic;
using ToolKit.Common;
using ToolKit.Tools;
using ToolKit.Tools.Common;
using Unity.Mathematics;

namespace ToolKit.DataStructure
{
    public class QuadTree<T> : IQuadTreeDebugInfo, IDisposable where T : IBoundable
    {
        private enum Quadrant
        {
            None = -1,
            TR,
            TL,
            BL,
            BR,
        }

        public class TreeNode : ISetupable, IClearable, IDisposable, ITraversableNode
        {
            public TreeNode Parent { get; internal set; }

            /// <summary>
            /// 当前节点，在父节点中的序号
            /// </summary>
            public int ChildIdx { get; internal set; }

            public bool IsRoot => Parent == null;

            public TreeNode[] Children { get; private set; } = new TreeNode[4];
            internal List<T> Values { get; private set; } = new List<T>(8);

            /// <summary>
            /// 节点中存储的值（只读视图）
            /// </summary>
            public IReadOnlyList<T> NodeValues => Values;

            public int Depth { get; internal set; } = -1;
            public AABBBox NodeBox { get; internal set; }
            public bool IsInPool { get; private set; } = false;

            public int Count { get; } = 4;

            public void Setup()
            {
                IsInPool = false;
            }

            public void Setup(AABBBox box, int depth, TreeNode parent, int childIdx = -1)
            {
                NodeBox = box;
                Depth = depth;
                Parent = parent;
                ChildIdx = childIdx;
            }

            public ITraversableNode GetChild(int index)
            {
                return Children[index];
            }

            public void Clear()
            {
                for (int i = 0; i < Children.Length; i++)
                {
                    Children[i] = null;
                }

                Parent = null;
                ChildIdx = -1;

                Values.Clear();
                Depth = -1;
                IsInPool = true;
            }

            public void Dispose()
            {
                foreach (var child in Children)
                {
                    child?.Dispose();
                }

                Children = null;
                Parent = null;

                Values.Clear();
                Values = null;
            }
        }

        /// <summary>
        /// 节点在尝试分裂前可容纳的最大值数量
        /// </summary>
        public int ValueThreshold { get; }

        /// <summary>
        /// 节点的最大深度，当节点达到MaxDepth时，我们停止尝试分裂，因为过度细分可能会影响性能
        /// </summary>
        public int MaxDepth { get; }

        /// <summary>
        /// 更新树时，强制重建树的上限系数
        /// </summary>
        public float RebuildThreshold { get; }

        /// <summary>
        /// 树中存储的元素数量
        /// </summary>
        public int Count => _valueList.Count;

        private TreeNode _rootNode;
        private SimplePool<TreeNode> _nodePool;
        private List<T> _valueList;
        private Dictionary<T, TreeNode> _valueNodeMap;

        /// <param name="rootBox">四叉树根节点的包围盒范围</param>
        /// <param name="valueThreshold">节点分裂前可容纳的最大值数量，默认16</param>
        /// <param name="maxDepth">四叉树的最大深度，默认8</param>
        /// <param name="rebuildThreshold">局部更新/全量重建的切换系数，默认1.5</param>
        public QuadTree(AABBBox rootBox, int valueThreshold = 16, int maxDepth = 8, float rebuildThreshold = 1.5f)
        {
            ValueThreshold = valueThreshold;
            MaxDepth = maxDepth;
            RebuildThreshold = rebuildThreshold;
            _valueList = new List<T>(valueThreshold * 16);
            _valueNodeMap = new Dictionary<T, TreeNode>(valueThreshold * 16);
            _nodePool = new SimplePool<TreeNode>();
            _rootNode = _nodePool.Pop();
            _rootNode.Setup(rootBox, 0, null, 0);
        }

        private static bool IsLeaf(TreeNode node)
        {
            return node.Children[0] == null;
        }

        /// <summary>
        /// 根据父节点的盒子和象限索引计算子节点的盒子
        /// </summary>
        private static AABBBox ComputeBox(AABBBox box, int index)
        {
            var center = box.Center;
            var min = box.Min;
            var max = box.Max;
            switch (index)
            {
                case (int)Quadrant.TR:
                    return new AABBBox(center, max);
                case (int)Quadrant.TL:
                    return new AABBBox(new float2(min.x, center.y),
                        new float2(center.x, max.y));
                case (int)Quadrant.BL:
                    return new AABBBox(min, center);
                case (int)Quadrant.BR:
                    return new AABBBox(new float2(center.x, min.y),
                        new float2(max.x, center.y));
                default:
                    throw new IndexOutOfRangeException();
            }
        }

        /// <summary>
        /// 获取象限
        /// </summary>
        private static int GetBoxQuadrant(AABBBox nodeBox, AABBBox valueBox)
        {
            var ret = Quadrant.None;
            if (valueBox.Right < nodeBox.Center.x)
            {
                if (valueBox.Bottom > nodeBox.Center.y)
                {
                    ret = Quadrant.TL;
                }
                else if (valueBox.Top <= nodeBox.Center.y)
                {
                    ret = Quadrant.BL;
                }
            }
            else if (valueBox.Left >= nodeBox.Center.x)
            {
                if (valueBox.Bottom > nodeBox.Center.y)
                {
                    ret = Quadrant.TR;
                }
                else if (valueBox.Top <= nodeBox.Center.y)
                {
                    ret = Quadrant.BR;
                }
            }

            return (int)ret;
        }

        /// <summary>
        /// 添加节点
        /// </summary>
        private void AddValue(TreeNode node, AABBBox nodeBox, int depth, T value)
        {
            Log.Assert(nodeBox.Contains(value.GetBoundaryBox()),
                $"Child box {value.GetBoundaryBox()} must be contained in parent box {nodeBox}!");

            // 如果节点是叶子节点，并且我们可以在其中插入新值（即达到MaxDepth或未达到Threshold），则插入。否则，分裂该节点并重新尝试插入。
            if (IsLeaf(node))
            {
                if (depth >= MaxDepth || node.Values.Count < ValueThreshold)
                {
                    node.Values.Add(value);
                    _valueNodeMap[value] = node;
                }
                else
                {
                    SplitNode(node, node.NodeBox, depth + 1);
                    AddValue(node, node.NodeBox, depth, value);
                }
            }
            else
            {
                var idx = GetBoxQuadrant(nodeBox, value.GetBoundaryBox());
                if (idx != -1) // 如果值完全包含在某个子节点中，则将其添加到该子节点
                {
                    AddValue(node.Children[idx], node.Children[idx].NodeBox, depth + 1, value);
                }
                else
                {
                    node.Values.Add(value);
                    _valueNodeMap[value] = node;
                }
            }
        }

        /// <summary>
        /// 分裂节点
        /// </summary>
        private void SplitNode(TreeNode leafNode, AABBBox nodeBox, int depth)
        {
            // 创建4个子节点
            for (int i = 0; i < leafNode.Children.Length; i++)
            {
                var node = _nodePool.Pop();
                node.Setup(ComputeBox(nodeBox, i), depth, leafNode, i);
                leafNode.Children[i] = node;
            }

            // 原地压缩：将可分配到子节点的值移入对应子节点，无法分配的保留在当前节点
            int writeIdx = 0;
            for (int i = 0; i < leafNode.Values.Count; i++)
            {
                var val = leafNode.Values[i];
                var idx = GetBoxQuadrant(nodeBox, val.GetBoundaryBox());
                if (idx != -1)
                {
                    leafNode.Children[idx].Values.Add(val);
                    _valueNodeMap[val] = leafNode.Children[idx];
                }
                else
                {
                    leafNode.Values[writeIdx++] = val;
                }
            }

            if (writeIdx < leafNode.Values.Count)
            {
                leafNode.Values.RemoveRange(writeIdx, leafNode.Values.Count - writeIdx);
            }
        }

        private bool RemoveValue(TreeNode node, AABBBox nodeBox, T value)
        {
            if (IsLeaf(node))
            {
                // 从节点中删除值
                RemoveValueFromNode(node, value);
                return true;
            }
            else
            {
                var idx = GetBoxQuadrant(nodeBox, value.GetBoundaryBox());
                if (idx != -1) // 如果值完全包含在某个子节点中，则从该子节点中删除
                {
                    if (RemoveValue(node.Children[idx], node.Children[idx].NodeBox, value))
                    {
                        return TryMerge(node);
                    }

                    return false;
                }
                else
                {
                    RemoveValueFromNode(node, value);
                    return false;
                }
            }
        }

        private void RemoveValueFromNode(TreeNode node, T value)
        {
            Log.Assert(node.Values.Contains(value), $"Value not found: {value}");
            node.Values.Remove(value);
        }

        /// <summary>
        /// 将子节点中的值回收到父节点，并且回收子节点
        /// </summary>
        private bool TryMerge(TreeNode node)
        {
            Log.Assert(!IsLeaf(node), "Cannot merge a leaf node!");

            // 检查所有子节点是否为叶子节点，并且其自身的值与子节点的值总数是否低于阈值。
            // 如果是，则将子节点的所有值复制到当前节点，并删除子节点。
            // 如果节点合并成功，返回true，以便其父节点也尝试与子节点合并。
            var nbValues = node.Values.Count;
            foreach (var child in node.Children)
            {
                if (!IsLeaf(child))
                {
                    return false;
                }

                nbValues += child.Values.Count;
            }

            if (nbValues <= ValueThreshold)
            {
                for (int i = 0; i < node.Children.Length; i++)
                {
                    foreach (var val in node.Children[i].Values)
                    {
                        _valueNodeMap[val] = node;
                    }
                    node.Values.AddRange(node.Children[i].Values);
                    _nodePool.Push(node.Children[i]);
                    node.Children[i] = null;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// 查询区域内的值
        /// </summary>
        private static void Query(TreeNode node, AABBBox nodeBox, AABBBox queryBox, List<T> retList)
        {
            foreach (var val in node.Values)
            {
                if (queryBox.Intersects(val.GetBoundaryBox()))
                {
                    retList.Add(val);
                }
            }

            if (!IsLeaf(node))
            {
                for (int i = 0; i < node.Children.Length; i++)
                {
                    var childBox = node.Children[i].NodeBox;
                    if (queryBox.Intersects(childBox))
                    {
                        Query(node.Children[i], childBox, queryBox, retList);
                    }
                }
            }
        }

        /// <summary>
        /// 查找树中所有的相交节点
        /// </summary>
        private static void FindAllIntersections(TreeNode node, List<KeyValuePair<T, T>> retList)
        {
            /* 相交只能发生在：
             * 1、同一节点中存储的两个值之间；
             * 2、一个节点中存储的一个值与该节点的子节点中存储的另一个值之间；
             */
            for (int i = 0; i < node.Values.Count; i++)
            {
                var valBox = node.Values[i].GetBoundaryBox();
                for (int j = i + 1; j < node.Values.Count; j++)
                {
                    if (valBox.Intersects(node.Values[j].GetBoundaryBox()))
                    {
                        retList.Add(new KeyValuePair<T, T>(node.Values[i], node.Values[j]));
                    }
                }
            }

            if (!IsLeaf(node))
            {
                foreach (var val in node.Values)
                {
                    foreach (var child in node.Children)
                    {
                        FindIntersectionsInDescendants(child, val, retList);
                    }
                }

                foreach (var child in node.Children)
                {
                    FindAllIntersections(child, retList);
                }
            }
        }

        private static void FindIntersectionsInDescendants(TreeNode node, T value, List<KeyValuePair<T, T>> retDic)
        {
            var valBox = value.GetBoundaryBox();
            foreach (var other in node.Values)
            {
                if (other.GetBoundaryBox().Intersects(valBox))
                {
                    retDic.Add(new KeyValuePair<T, T>(value, other));
                }
            }

            if (!IsLeaf(node))
            {
                foreach (var child in node.Children)
                {
                    FindIntersectionsInDescendants(child, value, retDic);
                }
            }
        }

        /// <summary>
        /// 添加元素
        /// </summary>
        public void Add(T value)
        {
            AddValue(_rootNode, _rootNode.NodeBox, 0, value);
            _valueList.Add(value);
        }

        /// <summary>
        /// 移除元素
        /// </summary>
        public bool Remove(T value)
        {
            if (!_valueList.Remove(value))
            {
                return false;
            }

            RemoveValue(_rootNode, _rootNode.NodeBox, value);
            _valueNodeMap.Remove(value);
            return true;
        }

        /// <summary>
        /// 树中是否包含指定元素
        /// </summary>
        public bool Contains(T value)
        {
            return _valueList.Contains(value);
        }

        /// <summary>
        /// 查找区域内的所有值
        /// </summary>
        /// <param name="queryBox">查询范围</param>
        /// <returns>查询结果列表</returns>
        public List<T> Query(AABBBox queryBox)
        {
            var l = new List<T>();
            Query(_rootNode, _rootNode.NodeBox, queryBox, l);
            return l;
        }

        /// <summary>
        /// 查找区域内的所有值
        /// </summary>
        /// <param name="queryBox">查询范围</param>
        /// <param name="retList">查询结果的列表</param>
        public void Query(AABBBox queryBox, List<T> retList)
        {
            Query(_rootNode, _rootNode.NodeBox, queryBox, retList);
        }

        /// <summary>
        /// 查找树中所有的相交节点
        /// </summary>
        public void FindAllIntersections(List<KeyValuePair<T, T>> retDictionary)
        {
            FindAllIntersections(_rootNode, retDictionary);
        }

        public List<KeyValuePair<T, T>> FindAllIntersections()
        {
            var l = new List<KeyValuePair<T, T>>();
            FindAllIntersections(_rootNode, l);
            return l;
        }


        public TreeNode Find(T value)
        {
            _valueNodeMap.TryGetValue(value, out var node);
            return node;
        }

        /// <summary>
        /// 局部更新单个元素在树中的位置
        /// </summary>
        public void UpdateValue(T updateVal)
        {
            UpdateTreePartially(updateVal);
        }

        /// <summary>
        /// 批量更新元素位置。若更新数量超过阈值系数，则执行全量重建
        /// </summary>
        public void UpdateValues(List<T> updateList)
        {
            if (updateList == null || updateList.Count == 0)
            {
                return;
            }

            if (updateList.Count < _valueList.Count * RebuildThreshold)
            {
                foreach (var val in updateList)
                {
                    UpdateTreePartially(val);
                }
            }
            else
            {
                RebuildTree();
            }
        }

        /// <summary>
        /// 整体重新构建四叉树
        /// </summary>
        public void RebuildTree()
        {
            var tempValues = new List<T>(_valueList);
            _valueList.Clear();
            _valueNodeMap.Clear();
            ClearTree();
            foreach (var val in tempValues)
            {
                Add(val);
            }
        }

        /// <summary>
        /// 局部调整四叉树
        /// </summary>
        private void UpdateTreePartially(T updateVal)
        {
            /*
             *  1、节点仍然包含新值：
             *      1.1、新值计算的序号是-1，不需要移动；
             *      1.2、下沉到对应子节点中（分裂节点，合并节点）；
             *  2、节点不包含新值：
             *      2.1、父节点不包含新值（比如在父节点的兄弟节点上），直接Add
             *      2.2、父节点包含新值：
             *          2.2.1、新值在父节点计算的序号是-1，上浮到父节点（根节点判断）；
             *          2.2.2、移动到对应的兄弟节点中；
             */
            var node = Find(updateVal);
            Log.Assert(node != null, $"Value {updateVal} don't exist in the tree!");
            var newIdx = -1;
            TreeNode addNode = null;
            if (node.NodeBox.Contains(updateVal.GetBoundaryBox()))
            {
                newIdx = GetBoxQuadrant(node.NodeBox, updateVal.GetBoundaryBox());
                if (newIdx != -1)
                {
                    node.Values.Remove(updateVal);
                    if (!IsLeaf(node)) // 不能写到下面的else里面，因为merge后，可能children空了
                    {
                        TryMerge(node);
                    }

                    if (IsLeaf(node))
                    {
                        SplitNode(node, node.NodeBox, node.Depth + 1);
                        addNode = node;
                    }
                    else
                    {
                        addNode = node.Children[newIdx];
                    }

                    AddValue(addNode, addNode.NodeBox, addNode.Depth, updateVal);
                }
            }
            else
            {
                node.Values.Remove(updateVal);
                if (!IsLeaf(node))
                {
                    TryMerge(node);
                }

                // 向上查找包含新位置的祖先节点
                var ancestor = node.Parent;
                while (ancestor != null && !ancestor.NodeBox.Contains(updateVal.GetBoundaryBox()))
                {
                    ancestor = ancestor.Parent;
                }

                if (ancestor != null)
                {
                    newIdx = GetBoxQuadrant(ancestor.NodeBox, updateVal.GetBoundaryBox());
                    addNode = (IsLeaf(ancestor) || newIdx == -1) ? ancestor : ancestor.Children[newIdx];
                    AddValue(addNode, addNode.NodeBox, addNode.Depth, updateVal);
                }
                else
                {
                    // 已超出根节点范围，从根节点重新添加（会触发 Assert 提示越界）
                    AddValue(_rootNode, _rootNode.NodeBox, 0, updateVal);
                }
            }
        }

        /// <summary>
        /// 层序遍历
        /// </summary>
        public List<ITraversableNode> LevelOrderTraverse()
        {
            return TraversalHelper.LevelOrderTraverse(_rootNode);
        }

        public void PrintTree()
        {
            PrintTree(_rootNode);
        }

        private void PrintTree(TreeNode node, int depth = 0)
        {
            var indent = new string(' ', depth * 8);
            Log.Debug($"{indent} Node {node.ChildIdx}: {node.NodeBox}");
            if (IsLeaf(node))
            {
                return;
            }
            foreach (var child in node.Children)
            {
                PrintTree(child, depth + 1);
            }
        }

        public void ClearTree()
        {
            if (IsLeaf(_rootNode))
            {
                _rootNode.Values.Clear();
                return;
            }

            // 只回收子节点，不回收根节点
            for (int i = 0; i < _rootNode.Children.Length; i++)
            {
                if (_rootNode.Children[i] != null)
                {
                    TraversalHelper.PostorderTraverse(_rootNode.Children[i], RecycleNode);
                    _rootNode.Children[i] = null;
                }
            }

            _rootNode.Values.Clear();
            _valueNodeMap.Clear();
        }

        private void RecycleNode(ITraversableNode node)
        {
            _nodePool.Push(node as TreeNode);
        }

        #region IQuadTreeDebugInfo 实现

        int IQuadTreeDebugInfo.ElementCount => Count;
        AABBBox IQuadTreeDebugInfo.RootBox => _rootNode.NodeBox;
        int IQuadTreeDebugInfo.ConfigMaxDepth => MaxDepth;
        int IQuadTreeDebugInfo.ConfigValueThreshold => ValueThreshold;

        void IQuadTreeDebugInfo.CollectDebugNodeInfos(List<QuadTreeNodeDebugInfo> result)
        {
            result.Clear();
            CollectNodeInfosRecursive(_rootNode, result);
        }

        private static void CollectNodeInfosRecursive(TreeNode node, List<QuadTreeNodeDebugInfo> result)
        {
            result.Add(new QuadTreeNodeDebugInfo
            {
                Box = node.NodeBox,
                Depth = node.Depth,
                ValueCount = node.Values.Count,
                IsLeaf = IsLeaf(node)
            });

            if (!IsLeaf(node))
            {
                for (int i = 0; i < node.Children.Length; i++)
                {
                    if (node.Children[i] != null)
                        CollectNodeInfosRecursive(node.Children[i], result);
                }
            }
        }

        void IQuadTreeDebugInfo.CollectDebugElementBoxes(List<AABBBox> result)
        {
            result.Clear();
            foreach (var val in _valueList)
            {
                result.Add(val.GetBoundaryBox());
            }
        }

        #endregion

        public void Dispose()
        {
            ClearTree();
            _rootNode.Dispose();
            _rootNode = null;

            _nodePool.Dispose();
            _nodePool = null;

            _valueList.Clear();
            _valueList = null;

            _valueNodeMap.Clear();
            _valueNodeMap = null;
        }
    }
}