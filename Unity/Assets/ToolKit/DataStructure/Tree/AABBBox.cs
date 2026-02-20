using ToolKit.Tools;
using Unity.Mathematics;

namespace ToolKit.DataStructure
{
    public struct AABBBox
    {
        private float2 _center;
        public float2 Center => _center;
        public float2 Size { get; private set; }
        private float2 _halfSize;
        public float2 Min { get; private set; }
        public float2 Max { get; private set; }
        public float Top => Max.y;
        public float Bottom => Min.y;
        public float Left => Min.x;
        public float Right => Max.x;
        public float Width => Size.x;
        public float Height => Size.y;
        
        public AABBBox(float2 point1, float2 point2, bool initByDiagonal = true) : this()
        {
            if (initByDiagonal)
            {
                Min = math.min(point1, point2);
                Max = math.max(point1, point2);
                Size = Max - Min;
                _halfSize = Size / 2;
                _center = Min + _halfSize;
            }
            else
            {
                _center = point1;
                Size = point2;
                _halfSize = Size / 2;
                Min = new float2(Center.x - _halfSize.x, Center.y - _halfSize.y);
                Max = new float2(Center.x + _halfSize.x, Center.y + _halfSize.y);
            }
        }

        public AABBBox(float minX, float minY, float width, float height)
            : this(new float2(minX, minY), new float2(minX + width, minY + height))
        {
        }

        public void UpdatePosition(float2 newCenter)
        {
            if (newCenter.Equals(_center))
            {
                return;
            }

            _center = newCenter;
            Min = new float2(Center.x - _halfSize.x, Center.y - _halfSize.y);
            Max = new float2(Center.x + _halfSize.x, Center.y + _halfSize.y);
        }

        public void UpdatePosition(float newX, float newY)
        {
            if (newX.Equals(_center.x) && newY.Equals(_center.y))
            {
                return;
            }

            _center.x = newX;
            _center.y = newY;
            Min = new float2(Center.x - _halfSize.x, Center.y - _halfSize.y);
            Max = new float2(Center.x + _halfSize.x, Center.y + _halfSize.y);
        }

        public void UpdateSize(float2 newSize)
        {
            if (newSize.Equals(Size))
            {
                return;
            }

            Size = newSize;
            _halfSize = Size / 2;
            Min = new float2(Center.x - _halfSize.x, Center.y - _halfSize.y);
            Max = new float2(Center.x + _halfSize.x, Center.y + _halfSize.y);
        }

        /// <summary>
        /// 矩形包含
        /// </summary>
        public bool Contains(AABBBox otherBox)
        {
            return Min.x <= otherBox.Min.x && Min.y <= otherBox.Min.y
                                           && Max.x >= otherBox.Max.x && Max.y >= otherBox.Max.y;
        }

        /// <summary>
        /// 矩形相交
        /// </summary>
        public bool Intersects(AABBBox otherBox)
        {
            var newMin = math.max(Min, otherBox.Min);
            var newMax = math.min(Max, otherBox.Max);
            return newMax.x >= newMin.x && newMax.y >= newMin.y;
        }

        public static AABBBox operator +(AABBBox box, float2 vector)
        {
            return new AABBBox(box.Center + vector, box.Size, false);
        }

        public static AABBBox operator -(AABBBox box, float2 vector)
        {
            return new AABBBox(box.Center - vector, box.Size, false);
        }

        public static AABBBox operator *(AABBBox box, float factor)
        {
            return new AABBBox(box.Center, box.Size * factor, false);
        }

        public static AABBBox operator *(AABBBox box, float2 factor)
        {
            return new AABBBox(box.Center, box.Size * factor, false);
        }

        public static AABBBox operator /(AABBBox box, float factor)
        {
            return new AABBBox(box.Center, box.Size / factor, false);
        }

        public static AABBBox operator /(AABBBox box, float2 factor)
        {
            return new AABBBox(box.Center, box.Size / factor, false);
        }

        public static bool operator ==(AABBBox a, AABBBox b)
        {
            return a.Center.Equals(b.Center) && a.Size.Equals(b.Size);
        }

        public static bool operator !=(AABBBox a, AABBBox b)
        {
            return !a.Center.Equals(b.Center) || !a.Size.Equals(b.Size);
        }

        public override bool Equals(object obj)
        {
            return obj is AABBBox other && this == other;
        }

        public override int GetHashCode()
        {
            return Center.GetHashCode() ^ (Size.GetHashCode() << 16);
        }

        public override string ToString()
        {
            return
                $"[({Min.x.ToString("0.000")},{Min.y.ToString("0.000")})->({Max.x.ToString("0.000")},{Max.y.ToString("0.000")})]";
        }
    }

    /// <summary>
    /// 可获取AABB包围盒的对象接口
    /// </summary>
    public interface IBoundable
    {
        public AABBBox GetBoundaryBox();
    }
}