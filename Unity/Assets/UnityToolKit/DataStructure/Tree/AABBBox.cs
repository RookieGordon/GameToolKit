using Unity.Mathematics;

namespace ToolKit.DataStructure
{
    public struct AABBBox
    {
        private float2 _center;
        public float2 Center => _center;
        public float2 Size { get; private set; }
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
                Size = point2 - point1;
                _center = new float2(point1.x + Size.x / 2.0f, point1.y + Size.y / 2.0f);
                Min = point1;
                Max = point2;
            }
            else
            {
                _center = point1;
                Size = point2;
                Min = new float2(Center.x - Size.x / 2.0f, Center.y - Size.y / 2.0f);
                Max = new float2(Center.x + Size.x / 2.0f, Center.y + Size.y / 2.0f);
            }
        }

        public AABBBox(float minX, float minY, float width, float height)
            : this(new float2(minX, minY), new float2(minX + width, minY + height))
        {
        }

        public void UpdatePosition(float2 newCenter)
        {
            _center = newCenter;
            Min = new float2(Center.x - Size.x / 2.0f, Center.y - Size.y / 2.0f);
            Max = new float2(Center.x + Size.x / 2.0f, Center.y + Size.y / 2.0f);
        }

        public void UpdatePosition(float newX, float newY)
        {
            _center.x = newX;
            _center.y = newY;
            Min = new float2(Center.x - Size.x / 2.0f, Center.y - Size.y / 2.0f);
            Max = new float2(Center.x + Size.x / 2.0f, Center.y + Size.y / 2.0f);
        }

        public void UpdateSize(float2 newSize)
        {
            Size = newSize;
            Min = new float2(Center.x - Size.x, Center.y - Size.y);
            Max = new float2(Center.x + Size.x, Center.y + Size.y);
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

        public override string ToString()
        {
            return $"[{Min}->{Max}]";
        }
    }
}