/*
 * author       : Gordon
 * datetime     : 2025/3/15
 * description  : 树的遍历工具  TODO 待测试！！
 */

using System;
using System.Collections.Generic;
using ToolKit.Tools;

namespace ToolKit.DataStructure
{
    public interface ITravelableNode
    {
        public int Count { get; }
        public ITravelableNode GetChild(int index);
    }

    public class TraversalHelper
    {
        private static Queue<ITravelableNode> _bfsQueue = new Queue<ITravelableNode>();

        /// <summary>
        /// 循环版本广度优先搜索
        /// </summary>
        /// <param name="rootNode">根节点</param>
        /// <param name="checkCondition">检查条件</param>
        /// <param name="resultList">符合条件的节点列表</param>
        /// <param name="finishIfFindOne">是否只查询第一个符合条件的节点（如果是，会直接结束搜索）</param>
        public static void BFS(ITravelableNode rootNode, Func<ITravelableNode, bool> checkCondition,
            List<ITravelableNode> resultList = null, bool finishIfFindOne = false)
        {
            _bfsQueue.Clear();
            _bfsQueue.Enqueue(rootNode);
            while (_bfsQueue.Count > 0)
            {
                var curNode = _bfsQueue.Dequeue();
                if (checkCondition(curNode))
                {
                    resultList?.Add(curNode);
                    if (finishIfFindOne)
                    {
                        return;
                    }
                }

                for (int i = 0; i < curNode.Count; i++)
                {
                    var childNode = curNode.GetChild(i);
                    if (childNode == null)
                    {
                        continue;
                    }

                    _bfsQueue.Enqueue(curNode.GetChild(i));
                }
            }
        }

        /// <summary>
        /// 深度优先搜索
        /// </summary>
        /// <param name="node"></param>
        /// <param name="checkCondition">检查条件</param>
        /// <param name="resultList">符合条件的节点列表</param>
        /// <param name="finishIfFindOne">是否只查询第一个符合条件的节点（如果是，会直接结束搜索）</param>
        /// <param name="visitedPath">第一个符合条件的节点的路径列表</param>
        public static void DFS(ITravelableNode node, Func<ITravelableNode, bool> checkCondition,
            List<ITravelableNode> resultList = null, bool finishIfFindOne = false,
            Stack<ITravelableNode> visitedPath = null)
        {
            visitedPath?.Push(node);
            if (checkCondition(node))
            {
                resultList?.Add(node);
                if (finishIfFindOne)
                {
                    return;
                }
            }

            for (int i = 0; i < node.Count; i++)
            {
                var childNode = node.GetChild(i);
                if (childNode == null)
                {
                    continue;
                }

                DFS(childNode, checkCondition, resultList, finishIfFindOne, visitedPath);
                if (resultList != null && resultList.Count == 1 && finishIfFindOne)
                {
                    return;
                }
            }

            visitedPath?.Pop();
        }

        /// <summary>
        /// 层序遍历（注意要和前序遍历做区分，层序遍历其实就是BFS）
        /// </summary>
        public static void LevelOrderTravel(ITravelableNode rootNode, Action<ITravelableNode> handle = null,
            List<ITravelableNode> resultList = null)
        {
            _bfsQueue.Clear();
            _bfsQueue.Enqueue(rootNode);
            while (_bfsQueue.Count > 0)
            {
                var curNode = _bfsQueue.Dequeue();
                resultList?.Add(curNode);
                if (handle != null)
                {
                    handle(curNode);
                }

                for (int i = 0; i < curNode.Count; i++)
                {
                    var childNode = curNode.GetChild(i);
                    if (childNode == null)
                    {
                        continue;
                    }

                    _bfsQueue.Enqueue(curNode.GetChild(i));
                }
            }
        }

        /// <summary>
        /// 前序遍历（根左右）
        /// </summary>
        public static void PreorderTravel(ITravelableNode node, Action<ITravelableNode> handle = null,
            List<ITravelableNode> resultList = null)
        {
            if (node == null)
            {
                return;
            }
            
            resultList?.Add(node);
            if (handle != null)
            {
                handle(node);
            }

            for (int i = 0; i < node.Count; i++)
            {
                var childNode = node.GetChild(i);
                if (childNode == null)
                {
                    continue;
                }

                PreorderTravel(childNode, handle, resultList);
            }
        }

        /// <summary>
        /// 中序遍历（仅支持二叉树，左根右）
        /// </summary>
        public static void InorderTravel(ITravelableNode node, Action<ITravelableNode> handle = null,
            List<ITravelableNode> resultList = null)
        {
            if (node == null)
            {
                return;
            }
            
            InorderTravel(node.GetChild(0), handle, resultList);
            resultList?.Add(node);
            if (handle != null)
            {
                handle(node);
            }
            InorderTravel(node.GetChild(1), handle, resultList);
        }

        /// <summary>
        /// 后续遍历（左右根）
        /// </summary>
        public static void PostorderTravel(ITravelableNode node, Action<ITravelableNode> handle = null,
            List<ITravelableNode> resultList = null)
        {
            if (node == null)
            {
                return;
            }
            for (int i = 0; i < node.Count; i++)
            {
                var childNode = node.GetChild(i);
                if (childNode == null)
                {
                    continue;
                }

                PostorderTravel(childNode, handle, resultList);
            }

            resultList?.Add(node);
            if (handle != null)
            {
                handle(node);
            }
        }
    }
}