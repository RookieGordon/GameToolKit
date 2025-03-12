/*
 * author       : Gordon
 * datetime     : 2025/3/10
 * description  : 四叉树，参考：https://pvigier.github.io/2019/08/04/quadtree-collision-detection.html
 */

using System;
using System.Collections.Generic;
using ToolKit.Common;
using ToolKit.Tools.Common;
using Unity.Mathematics;

namespace ToolKit.DataStructure
{
    public interface IBoundable
    {
        public AABBBox GetBoundaryBox();
    }

    public class QuadTree<T> where T : IBoundable
    {
        private enum Quadrant
        {
            None = -1,
            TR,
            TL,
            BL,
            BR,
        }

        public class TreeNode : ISetupable, IClearable, IDisposable
        {
            public TreeNode[] Children { get; private set; } = new TreeNode[4];
            public List<T> Values { get; internal set; } = new List<T>(VALUE_THRESHOLD);
            public int Depth { get; internal set; } = -1;
            public AABBBox NodeBox { get; internal set; }

            public void Setup()
            {
            }

            public void Setup(AABBBox box, int depth)
            {
                NodeBox = box;
                Depth = depth;
            }

            public void Clear()
            {
                for (int i = 0; i < Children.Length; i++)
                {
                    Children[i]?.Clear();
                    Children[i] = null;
                }

                Values.Clear();
                Depth = -1;
            }

            public void Dispose()
            {
                foreach (var child in Children)
                {
                    child?.Dispose();
                }

                Children = null;

                Values.Clear();
                Values = null;
            }
        }

        /// <summary>
        /// 节点在尝试分裂前可容纳的最大值数量
        /// </summary>
        public const int VALUE_THRESHOLD = 16;

        /// <summary>
        /// 节点的最大深度，当节点达到MaxDepth时，我们停止尝试分裂，因为过度细分可能会影响性能
        /// </summary>
        public const int MAX_DEPTH = 8;

        private TreeNode _rootNode;
        private SimplePool<TreeNode> _nodePool;

        public QuadTree(AABBBox rootBox)
        {
            _nodePool = new SimplePool<TreeNode>();
            _rootNode = _nodePool.Pop();
            _rootNode.Setup(rootBox, 0);
        }

        private static bool _IsLeaf(TreeNode node)
        {
            return node.Children[0] == null;
        }

        /// <summary>
        /// 根据父节点的盒子和象限索引计算子节点的盒子
        /// </summary>
        private static AABBBox _ComputeBox(AABBBox box, int index)
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
        private static int _GetBoxQuadrant(AABBBox nodeBox, AABBBox valueBox)
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
        private void _AddValue(TreeNode node, AABBBox nodeBox, int depth, T value)
        {
            if (!nodeBox.Contains(value.GetBoundaryBox()))
            {
                throw new Exception($"Child box {value.GetBoundaryBox()} must be contained in parent box {nodeBox}!");
            }

            // 如果节点是叶子节点，并且我们可以在其中插入新值（即达到MaxDepth或未达到Threshold），则插入。否则，分裂该节点并重新尝试插入。
            if (_IsLeaf(node))
            {
                if (depth >= MAX_DEPTH || node.Values.Count < VALUE_THRESHOLD)
                {
                    node.Values.Add(value);
                }
                else
                {
                    _SplitNode(node, node.NodeBox, depth + 1);
                    _AddValue(node, node.NodeBox, depth, value);
                }
            }
            else
            {
                var idx = _GetBoxQuadrant(nodeBox, value.GetBoundaryBox());
                if (idx != -1) // 如果值完全包含在某个子节点中，则将其添加到该子节点
                {
                    _AddValue(node.Children[idx], node.Children[idx].NodeBox, depth + 1, value);
                }
                else
                {
                    node.Values.Add(value);
                }
            }
        }

        /// <summary>
        /// 分裂节点
        /// </summary>
        private void _SplitNode(TreeNode leafNode, AABBBox nodeBox, int depth)
        {
            // 创建4个子节点
            for (int i = 0; i < leafNode.Children.Length; i++)
            {
                var node = _nodePool.Pop();
                node.Setup(_ComputeBox(nodeBox, i), depth);
                leafNode.Children[i] = node;
            }

            var newList = new List<T>();
            // 计算值所在象限，将其分配到子节点中。无法分配的，将其留在父节点中。
            foreach (var val in leafNode.Values)
            {
                var idx = _GetBoxQuadrant(nodeBox, val.GetBoundaryBox());
                if (idx != -1)
                {
                    leafNode.Children[idx].Values.Add(val);
                }
                else
                {
                    newList.Add(val);
                }
            }

            leafNode.Values = newList;
        }

        private bool _RemoveValue(TreeNode node, AABBBox nodeBox, T value)
        {
            if (_IsLeaf(node))
            {
                // 从节点中删除值
                _RemoveValueFromNode(node, value);
                return true;
            }
            else
            {
                var idx = _GetBoxQuadrant(nodeBox, value.GetBoundaryBox());
                if (idx != -1) // 如果值完全包含在某个子节点中，则从该子节点中删除
                {
                    if (_RemoveValue(node.Children[idx], node.Children[idx].NodeBox, value))
                    {
                        return _tryMerge(node);
                    }

                    return false;
                }
                else
                {
                    _RemoveValueFromNode(node, value);
                    return false;
                }
            }
        }

        private void _RemoveValueFromNode(TreeNode node, T value)
        {
            if (!node.Values.Contains(value))
            {
                throw new Exception($"Value not found: {value}");
            }
            node.Values.Remove(value);
        }

        /// <summary>
        /// 将子节点中的值回收到父节点，并且回收子节点
        /// </summary>
        private bool _tryMerge(TreeNode node)
        {
            if (_IsLeaf(node))
            {
                throw new Exception("Cannot merge a leaf node!");
            }

            // 检查所有子节点是否为叶子节点，并且其自身的值与子节点的值总数是否低于阈值。
            // 如果是，则将子节点的所有值复制到当前节点，并删除子节点。
            // 如果节点合并成功，返回true，以便其父节点也尝试与子节点合并。
            var nbValues = node.Values.Count;
            foreach (var child in node.Children)
            {
                if (!_IsLeaf(child))
                {
                    return false;
                }

                nbValues += child.Values.Count;
            }

            if (nbValues <= VALUE_THRESHOLD)
            {
                for (int i = 0; i < node.Children.Length; i++)
                {
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
        private static void _Query(TreeNode node, AABBBox nodeBox, AABBBox queryBox, List<T> retList)
        {
            foreach (var val in node.Values)
            {
                if (queryBox.Intersects(val.GetBoundaryBox()))
                {
                    retList.Add(val);
                }
            }

            if (!_IsLeaf(node))
            {
                for (int i = 0; i < node.Children.Length; i++)
                {
                    var childBox = node.Children[i].NodeBox;
                    if (queryBox.Intersects(childBox))
                    {
                        _Query(node.Children[i], childBox, queryBox, retList);
                    }
                }
            }
        }

        /// <summary>
        /// 查找树中所有的相交节点
        /// </summary>
        private static void _FindAllIntersections(TreeNode node, Dictionary<T, T> retDic)
        {
            /* 相交只能发生在：
             * 1、同一节点中存储的两个值之间；
             * 2、一个节点中存储的一个值与该节点的子节点中存储的另一个值之间；
             */
            for (int i = 0; i < node.Values.Count; i++)
            {
                var valBox = node.Values[i].GetBoundaryBox();
                for (int j = 0; j < node.Values.Count; j++)
                {
                    if (valBox.Intersects(node.Values[j].GetBoundaryBox()))
                    {
                        retDic.Add(node.Values[i], node.Values[j]);
                    }
                }
            }

            if (!_IsLeaf(node))
            {
                foreach (var val in node.Values)
                {
                    foreach (var child in node.Children)
                    {
                        _FindIntersectionsInDescendants(child, val, retDic);
                    }
                }

                foreach (var child in node.Children)
                {
                    _FindAllIntersections(child, retDic);
                }
            }
        }

        private static void _FindIntersectionsInDescendants(TreeNode node, T value, Dictionary<T, T> retDic)
        {
            var valBox = value.GetBoundaryBox();
            foreach (var other in node.Values)
            {
                if (other.GetBoundaryBox().Intersects(valBox))
                {
                    retDic.Add(value, other);
                }
            }

            if (!_IsLeaf(node))
            {
                foreach (var child in node.Children)
                {
                    _FindIntersectionsInDescendants(child, value, retDic);
                }
            }
        }

        /// <summary>
        /// 添加元素
        /// </summary>
        public void Add(T value)
        {
            _AddValue(_rootNode, _rootNode.NodeBox, 0, value);
        }

        /// <summary>
        /// 移除元素
        /// </summary>
        public bool Remove(T value)
        {
            return _RemoveValue(_rootNode, _rootNode.NodeBox, value);
        }

        /// <summary>
        /// 查找区域内的所有值
        /// </summary>
        /// <param name="queryBox">查询范围</param>
        /// <returns>查询结果列表</returns>
        public List<T> Query(AABBBox queryBox)
        {
            var l = new List<T>();
            _Query(_rootNode, _rootNode.NodeBox, queryBox, l);
            return l;
        }

        /// <summary>
        /// 查找区域内的所有值
        /// </summary>
        /// <param name="queryBox">查询范围</param>
        /// <param name="retList">查询结果的列表</param>
        public void Query(AABBBox queryBox, List<T> retList)
        {
            _Query(_rootNode, _rootNode.NodeBox, queryBox, retList);
        }

        /// <summary>
        /// 查找树中所有的相交节点
        /// </summary>
        public void FindAllIntersections(Dictionary<T, T> retDictionary)
        {
            _FindAllIntersections(_rootNode, retDictionary);
        }

        /// <summary>
        /// 层序遍历
        /// </summary>
        public List<TreeNode> SequenceTraversal()
        {
            var queue = new Queue<TreeNode>();
            var l = new List<TreeNode>();
            queue.Enqueue(_rootNode);
            l.Add(_rootNode);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (_IsLeaf(node))
                {
                    continue;
                }

                foreach (var child in node.Children)
                {
                    l.Add(child);
                    queue.Enqueue(child);
                }
            }

            return l;
        }
    }
}