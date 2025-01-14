using Unity.Mathematics;

namespace ToolKit.DataStruct
{
    public class QTAABB
    {
        public float2 Min { get; set; }
        public float2 Max { get; set; }
        public float2 Center => (Min + Max) / 2;

        public QTAABB(float2 min, float2 max)
        {
            Min = min;
            Max = max;
        }
    }
}