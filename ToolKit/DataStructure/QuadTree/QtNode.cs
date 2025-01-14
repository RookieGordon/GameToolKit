
using System;
using Unity.Mathematics;

namespace ToolKit.DataStruct
{
    public enum EAreaQuadrant
    {
        TR = 0, // 右上区域(第一象限)
        TL = 1, 
        BL = 2, 
        BR = 3, 
        None = 4 // 无效区域
    }

    public class QtNode<T>: IDisposable
    {
        public bool IsLeaf { get; set; } = true;
        public QTAABB Bound { get; set; }
        public QtNode<T>[] Children { get; set; }
        public QtNode<T> Parent { get; set; }
        public T[] Data { get; set; }
        public bool IsUsed { get; set; } = false;
        public EAreaQuadrant IdAsChild { get; set; } = EAreaQuadrant.None;

        public QtNode()
        {
            Setup();
        }

        public void Setup()
        {
            Children = new QtNode<T>[4];
            Data = new T[4];
        }

        public QtNode<T> AssignFrom(QtNode<T> otherNode)
        {
            IsLeaf = otherNode.IsLeaf;
            Bound = otherNode.Bound;
            Children = otherNode.Children;
            Parent = otherNode.Parent;
            Data = otherNode.Data;
            IsUsed = otherNode.IsUsed;
            IdAsChild = otherNode.IdAsChild;

            return this;
        }

        public int GetChildCount()
        {
            var count = 0;
            for (int i = 0; i < 4; i++)
            {
                if (Children[i] != null)
                {
                    count++;
                }
            }

            return count;
        }

        public int GetDataCount()
        {
            var count = 0;
            for (int i = 0; i < 4; i++)
            {
                if (Data[i] != null)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 计算一个点在当前节点的哪个象限
        /// </summary>
        public EAreaQuadrant GetPositionID(float2 position)
        {
            var x = position.x;
            var y = position.y;
            var cx = Bound.Center.x;
            var cy = Bound.Center.y;
            
            if (x > Bound.Max.x || x < Bound.Min.x || y > Bound.Max.y || y < Bound.Min.y)
            {
                return EAreaQuadrant.None;
            }
            
            if (x >= cx)
            {
                return y >= cy ? EAreaQuadrant.TR : EAreaQuadrant.BR;
            }
            else
            {
                return y >= cy ? EAreaQuadrant.TL : EAreaQuadrant.BL;
            }
        }

        public void Dispose()
        {
            Bound = null;
            Children = null;
            Parent = null;
            Data = null;
        }
    }
}