/*
 * author       : Gordon
 * datetime     : 2025/3/15
 * description  : 树的遍历与搜索工具
 */

using System;
using System.Collections.Generic;

namespace ToolKit.DataStructure
{
    public interface ITraversableNode
    {
        int Count { get; }
        ITraversableNode GetChild(int index);
    }

    /// <summary>
    /// 树的遍历与搜索工具
    /// <para>搜索方法：BFSFindFirst / BFSFindAll / DFSFindFirst / DFSFindAll</para>
    /// <para>遍历方法：LevelOrderTraverse / PreorderTraverse / InorderTraverse / PostorderTraverse</para>
    /// </summary>
    public static class TraversalHelper
    {
        #region Search（搜索）

        /// <summary>
        /// 广度优先搜索 - 查找第一个符合条件的节点
        /// </summary>
        /// <param name="rootNode">根节点</param>
        /// <param name="predicate">匹配条件</param>
        /// <returns>第一个符合条件的节点，未找到返回 null</returns>
        public static ITraversableNode BFSFindFirst(ITraversableNode rootNode,
            Func<ITraversableNode, bool> predicate)
        {
            if (rootNode == null) return null;

            var queue = new Queue<ITraversableNode>();
            queue.Enqueue(rootNode);
            while (queue.Count > 0)
            {
                var curNode = queue.Dequeue();
                if (predicate(curNode))
                {
                    return curNode;
                }

                for (int i = 0; i < curNode.Count; i++)
                {
                    var child = curNode.GetChild(i);
                    if (child != null)
                    {
                        queue.Enqueue(child);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 广度优先搜索 - 查找所有符合条件的节点
        /// </summary>
        /// <param name="rootNode">根节点</param>
        /// <param name="predicate">匹配条件</param>
        /// <returns>所有符合条件的节点列表</returns>
        public static List<ITraversableNode> BFSFindAll(ITraversableNode rootNode,
            Func<ITraversableNode, bool> predicate)
        {
            var result = new List<ITraversableNode>();
            if (rootNode == null) return result;

            var queue = new Queue<ITraversableNode>();
            queue.Enqueue(rootNode);
            while (queue.Count > 0)
            {
                var curNode = queue.Dequeue();
                if (predicate(curNode))
                {
                    result.Add(curNode);
                }

                for (int i = 0; i < curNode.Count; i++)
                {
                    var child = curNode.GetChild(i);
                    if (child != null)
                    {
                        queue.Enqueue(child);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 深度优先搜索 - 查找第一个符合条件的节点
        /// </summary>
        /// <param name="node">起始节点</param>
        /// <param name="predicate">匹配条件</param>
        /// <param name="visitedPath">找到时保留从根到目标的路径（Stack 顶部为目标节点）</param>
        /// <returns>第一个符合条件的节点，未找到返回 null</returns>
        public static ITraversableNode DFSFindFirst(ITraversableNode node,
            Func<ITraversableNode, bool> predicate,
            Stack<ITraversableNode> visitedPath = null)
        {
            if (node == null) return null;

            visitedPath?.Push(node);
            if (predicate(node))
            {
                return node;
            }

            for (int i = 0; i < node.Count; i++)
            {
                var child = node.GetChild(i);
                if (child == null) continue;

                var found = DFSFindFirst(child, predicate, visitedPath);
                if (found != null)
                {
                    return found;
                }
            }

            visitedPath?.Pop();
            return null;
        }

        /// <summary>
        /// 深度优先搜索 - 查找所有符合条件的节点
        /// </summary>
        /// <param name="node">起始节点</param>
        /// <param name="predicate">匹配条件</param>
        /// <returns>所有符合条件的节点列表（前序顺序）</returns>
        public static List<ITraversableNode> DFSFindAll(ITraversableNode node,
            Func<ITraversableNode, bool> predicate)
        {
            var result = new List<ITraversableNode>();
            if (node == null) return result;
            DFSFindAllInternal(node, predicate, result);
            return result;
        }

        private static void DFSFindAllInternal(ITraversableNode node,
            Func<ITraversableNode, bool> predicate, List<ITraversableNode> result)
        {
            if (predicate(node))
            {
                result.Add(node);
            }

            for (int i = 0; i < node.Count; i++)
            {
                var child = node.GetChild(i);
                if (child != null)
                {
                    DFSFindAllInternal(child, predicate, result);
                }
            }
        }

        #endregion

        #region Traversal（遍历）

        /// <summary>
        /// 层序遍历（BFS 顺序）
        /// </summary>
        /// <param name="rootNode">根节点</param>
        /// <param name="action">对每个节点执行的操作</param>
        public static void LevelOrderTraverse(ITraversableNode rootNode,
            Action<ITraversableNode> action)
        {
            if (rootNode == null) return;

            var queue = new Queue<ITraversableNode>();
            queue.Enqueue(rootNode);
            while (queue.Count > 0)
            {
                var curNode = queue.Dequeue();
                action(curNode);

                for (int i = 0; i < curNode.Count; i++)
                {
                    var child = curNode.GetChild(i);
                    if (child != null)
                    {
                        queue.Enqueue(child);
                    }
                }
            }
        }

        /// <summary>
        /// 层序遍历 - 返回节点列表
        /// </summary>
        /// <param name="rootNode">根节点</param>
        /// <returns>按层序排列的节点列表</returns>
        public static List<ITraversableNode> LevelOrderTraverse(ITraversableNode rootNode)
        {
            var result = new List<ITraversableNode>();
            LevelOrderTraverse(rootNode, node => result.Add(node));
            return result;
        }

        /// <summary>
        /// 前序遍历（根左右）
        /// </summary>
        /// <param name="node">起始节点</param>
        /// <param name="action">对每个节点执行的操作</param>
        public static void PreorderTraverse(ITraversableNode node,
            Action<ITraversableNode> action)
        {
            if (node == null) return;

            action(node);

            for (int i = 0; i < node.Count; i++)
            {
                var child = node.GetChild(i);
                if (child != null)
                {
                    PreorderTraverse(child, action);
                }
            }
        }

        /// <summary>
        /// 前序遍历 - 返回节点列表
        /// </summary>
        public static List<ITraversableNode> PreorderTraverse(ITraversableNode node)
        {
            var result = new List<ITraversableNode>();
            PreorderTraverse(node, n => result.Add(n));
            return result;
        }

        /// <summary>
        /// 中序遍历（仅支持二叉树，左根右）
        /// </summary>
        /// <param name="node">起始节点</param>
        /// <param name="action">对每个节点执行的操作</param>
        public static void InorderTraverse(ITraversableNode node,
            Action<ITraversableNode> action)
        {
            if (node == null) return;

            if (node.Count > 0)
            {
                InorderTraverse(node.GetChild(0), action);
            }

            action(node);

            if (node.Count > 1)
            {
                InorderTraverse(node.GetChild(1), action);
            }
        }

        /// <summary>
        /// 中序遍历 - 返回节点列表
        /// </summary>
        public static List<ITraversableNode> InorderTraverse(ITraversableNode node)
        {
            var result = new List<ITraversableNode>();
            InorderTraverse(node, n => result.Add(n));
            return result;
        }

        /// <summary>
        /// 后序遍历（左右根）
        /// </summary>
        /// <param name="node">起始节点</param>
        /// <param name="action">对每个节点执行的操作</param>
        public static void PostorderTraverse(ITraversableNode node,
            Action<ITraversableNode> action)
        {
            if (node == null) return;

            for (int i = 0; i < node.Count; i++)
            {
                var child = node.GetChild(i);
                if (child != null)
                {
                    PostorderTraverse(child, action);
                }
            }

            action(node);
        }

        /// <summary>
        /// 后序遍历 - 返回节点列表
        /// </summary>
        public static List<ITraversableNode> PostorderTraverse(ITraversableNode node)
        {
            var result = new List<ITraversableNode>();
            PostorderTraverse(node, n => result.Add(n));
            return result;
        }

        #endregion
    }
}