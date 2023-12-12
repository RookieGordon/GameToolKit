using Unity.Mathematics;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks
{
    public static class FloatUtil
    {
        public static Vector3 ToVector3(this float2 f)
        {
            return new Vector3(f.x, f.y, 0);
        }
        
        public static Vector3 ToVector3(this float3 f)
        {
            return new Vector3(f.x, f.y, f.z);
        }
        
        public static float2 Tofloat2(this Vector3 v)
        {
            return new float2(v.x, v.y);
        }
        
        public static float2 Tofloat2(this float3 f)
        {
            return new float2(f.x, f.y);
        }
        
        public static float3 Tofloat3(this float2 f)
        {
            return new float3(f.x, f.y, 0);
        }
    }
}
