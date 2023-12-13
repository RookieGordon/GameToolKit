using Unity.Mathematics;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks
{
    public static class MathUtils
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

        public static float GetQuaternionAngle(quaternion q1, quaternion q2)
        {
            float dotProduct = math.dot(math.normalize(q1), math.normalize(q2));
            float angle = math.acos(math.min(math.abs(dotProduct), 1f));
            return math.degrees(angle);
        }

        public static quaternion AngleAxis(float angleInDegrees, float3 axis)
        {
            float angleInRadians = math.radians(angleInDegrees);
            float3 normalizedAxis = math.normalize(axis);
            float sinAngle = math.sin(angleInRadians * 0.5f);
            float cosAngle = math.cos(angleInRadians * 0.5f);

            return new quaternion(normalizedAxis.x * sinAngle,
                normalizedAxis.y * sinAngle,
                normalizedAxis.z * sinAngle,
                cosAngle);
        }

        public static quaternion QuaternionEuler(float3 eulerAngles)
        {
            // 将欧拉角从度转换为弧度
            float3 radians = math.radians(eulerAngles);
            // 创建四元数
            return quaternion.EulerXYZ(radians);
        }

        public static quaternion FromToRotation(float3 from, float3 to)
        {
            // 检查向量是否为零向量
            if (math.lengthsq(from) < 0.0001f || math.lengthsq(to) < 0.0001f)
            {
                // 如果任一向量为零向量，则返回单位四元数
                return quaternion.identity;
            }
            from = math.normalize(from);
            to = math.normalize(to);
            float3 cross = math.cross(from, to);
            float dot = math.dot(from, to);
            // 如果from和to向量在方向上几乎相反，我们需要找到一个垂直于它们的向量来作为旋转轴
            if (dot < -0.999999f)
            {
                cross = math.abs(from.x) > math.abs(from.z) ? new float3(-from.y, from.x, 0.0f) : new float3(0.0f, -from.z, from.y);
                cross = math.normalize(cross);
            }
            float w = math.sqrt(math.lengthsq(from) * math.lengthsq(to)) + dot;
            quaternion result = new quaternion(cross.x, cross.y, cross.z, w);
            return math.normalize(result);
        }

        public static quaternion LookRotation(float3 forward, float3 up)
        {
            forward = math.normalize(forward);
            // 如果forward和up向量平行或接近平行，选择一个默认的up向量
            if (math.lengthsq(forward) < 0.0001f || math.abs(math.dot(forward, up)) >= 1.0f)
            {
                up = math.abs(forward.y) < 0.999f ? new float3(0, 1, 0) : new float3(1, 0, 0);
            }

            float3 right = math.normalize(math.cross(up, forward));
            // 再次计算up向量，以确保它垂直于forward和right
            up = math.cross(forward, right);

            // 构建一个旋转矩阵
            float4x4 matrix = new float4x4
            {
                c0 = new float4(right.x, right.y, right.z, 0.0f),
                c1 = new float4(up.x, up.y, up.z, 0.0f),
                c2 = new float4(forward.x, forward.y, forward.z, 0.0f),
                c3 = new float4(0.0f, 0.0f, 0.0f, 1.0f)
            };

            return new quaternion(matrix);
        }
        
        public static quaternion RotateTowards(quaternion from, quaternion to, float maxDegreesDelta)
        {
            // 计算两个四元数之间的角度
            float angle = math.degrees(math.acos(math.min(math.abs(math.dot(from, to)), 1f)) * 2f);

            // 如果没有需要旋转的角度，或者旋转角度小于最大角度变化值，则直接返回目标四元数
            if (angle == 0f || angle <= maxDegreesDelta)
            {
                return to;
            }

            // 计算插值的t值
            float t = math.min(1f, maxDegreesDelta / angle);

            // 执行球面线性插值
            return math.slerp(from, to, t);
        }
    }
}