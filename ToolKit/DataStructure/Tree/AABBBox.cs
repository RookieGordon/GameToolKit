using System.Diagnostics;
using Unity.Mathematics;

namespace ToolKit.DataStructure
{
    public enum EAreaQuadrant
    {
        TR = 0, // 右上区域(第一象限)
        TL = 1,
        BL = 2,
        BR = 3,
        None = 4 // 无效区域
    }
    
    public struct AABBBox
    {
        public float2 Min { get; private set; }
        public float2 Max { get; private set; }
        public float2 Center => (Min + Max) / 2.0f;
        public float Width => Max.x - Min.x;
        public float Height => Max.y - Min.y;

        public AABBBox(float2 min, float2 max)
        {
            Min = min;
            Max = max;
        }

        /// <summary>
        /// 点是否在AABB内
        /// </summary>
        public bool Contains(float2 point)
        {
            return (point.x >= Min.x && point.x <= Max.x)
                   && (point.y >= Min.y && point.y <= Max.y);
        }

        /// <summary>
        /// 是否包含另一个AABB
        /// </summary>
        public bool Contains(AABBBox other)
        {
            return (Min.x <= other.Min.x && Max.x >= other.Max.x)
                   && (Min.y <= other.Min.y && Max.y >= other.Max.y);
        }
        
        /// <summary>
        /// 是否和另一个AABB相交
        /// </summary>
        public bool Intersects(AABBBox other)
        {
            // 矩形相交的结果仍是矩形
            return MathF.Max(Min.x, other.Min.x) <= MathF.Min(Max.x, other.Max.x)
                   && MathF.Max(Min.y, other.Min.y) <= MathF.Min(Max.y, other.Max.y);
        }

        public AABBBox[] Split(float2 splitPoint)
        {
            var contains = Contains(splitPoint);
            Debug.Assert(contains, "split point not in AABB!");
            return new[]
            {
                new AABBBox(splitPoint, Max),
                new AABBBox(new float2(Min.x, splitPoint.y), new float2(splitPoint.x, Max.y)),
                new AABBBox(Min, splitPoint),
                new AABBBox(new float2(splitPoint.x, Min.y), new float2(Max.x, splitPoint.y)),
            };
        }

        /// <summary>
        /// 在AABB中获取点的象限
        /// </summary>
        public EAreaQuadrant GetLocationQuadrant(float2 point, bool includeEdge = true)
        {
            if (!Contains(point))
            {
                return EAreaQuadrant.None;
            }

            if (point.x >= Center.x)
            {
                return point.y >= Center.y ? EAreaQuadrant.TR : EAreaQuadrant.BR;
            }
            else
            {
                return point.y >= Center.y ? EAreaQuadrant.TL : EAreaQuadrant.BL;
            }
        }
    }
}