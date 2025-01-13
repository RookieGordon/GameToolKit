using System;
using ToolKit.CommonTools;
using Unity.Mathematics;

namespace ToolKit.Common.DataStruct
{
    public class QTTree<T> : IDisposable
    {
        public QtNode<T>[] Root { get; set; }

        /// <summary>
        /// 被使用的节点最后一个索引
        /// </summary>
        public int LastUsedIdx { get; set; } = 0;

        public QTTree(int maxNodeCount)
        {
            Root = new QtNode<T>[maxNodeCount];
            LastUsedIdx = 0;
        }

        public void SetRoot(float2 mapMinPosition, float2 mapMaxPosition)
        {
            Root[0] = new QtNode<T>()
            {
                Bound = new QTAABB(mapMinPosition, mapMaxPosition),
                IsUsed = true,
            };
        }

        public QtNode<T> GetNode()
        {
            if (LastUsedIdx >= Root.Length - 1)
            {
                throw new Exception("QTTree.GetNode: Root is full!");
                return null;
            }

            return Root[LastUsedIdx++];
        }

        /// <summary>
        /// 移除并且回收节点
        /// </summary>
        public void RemoveNode(QtNode<T> node)
        {
        }
        
        /// <summary>
        /// 添加一个节点
        /// </summary>
        public void AddNode(QtNode<T> node)
        {
        }

        public void Dispose()
        {
            Root = null;
        }
    }
}