using Unity.Mathematics;

namespace BehaviorDesigner.Runtime
{
    public static class MathUtils
    {
        public static float2 Tofloat2(this float3 f)
        {
            return new float2(f.x, f.y);
        }

        public static float3 Tofloat3(this float2 f)
        {
            return new float3(f.x, f.y, 0);
        }
        
        /// <summary>
        ///   <para>Returns the angle in degrees between two rotations a and b.</para>
        /// </summary>
        /// TODO test with Quaternion.Angle
        public static float GetQuaternionAngle(quaternion q1, quaternion q2)
        {
            float dotProduct = math.dot(math.normalize(q1), math.normalize(q2));
            float angle = math.acos(math.min(math.abs(dotProduct), 1f));
            return math.degrees(angle);
        }

        /// <summary>
        ///   <para>Creates a rotation which rotates angle degrees around axis.</para>
        /// </summary>
        /// /// TODO test with Quaternion.AngleAxis
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

        /// <summary>
        ///   <para>Returns a rotation that rotates z degrees around the z axis, x degrees around the x axis, and y degrees around the y axis.</para>
        /// </summary>
        /// TODO test with Quaternion.Euler
        public static quaternion QuaternionEuler(float3 eulerAngles)
        {
            // 将欧拉角从度转换为弧度
            float3 radians = math.radians(eulerAngles);
            // 创建四元数
            return quaternion.EulerXYZ(radians);
        }

        /// <summary>
        ///   <para>Creates a rotation which rotates from fromDirection to toDirection.</para>
        /// </summary>
        /// TODO test with Quaternion.FromToRotation
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

        /// <summary>
        ///   <para>Creates a rotation with the specified forward and upwards directions.</para>
        /// </summary>
        /// <param name="forward">The direction to look in.</param>
        /// <param name="upwards">The vector that defines in which direction up is.</param>
        /// TODO test with Quaternion.LookRotation
        public static quaternion LookRotation(float3 forward, float3 upwards)
        {
            forward = math.normalize(forward);
            // 如果forward和up向量平行或接近平行，选择一个默认的up向量
            if (math.lengthsq(forward) < 0.0001f || math.abs(math.dot(forward, upwards)) >= 1.0f)
            {
                upwards = math.abs(forward.y) < 0.999f ? new float3(0, 1, 0) : new float3(1, 0, 0);
            }

            float3 right = math.normalize(math.cross(upwards, forward));
            // 再次计算up向量，以确保它垂直于forward和right
            upwards = math.cross(forward, right);

            // 构建一个旋转矩阵
            float4x4 matrix = new float4x4
            {
                c0 = new float4(right.x, right.y, right.z, 0.0f),
                c1 = new float4(upwards.x, upwards.y, upwards.z, 0.0f),
                c2 = new float4(forward.x, forward.y, forward.z, 0.0f),
                c3 = new float4(0.0f, 0.0f, 0.0f, 1.0f)
            };

            return new quaternion(matrix);
        }
        
        /// <summary>
        ///   <para>Rotates a rotation from towards to.</para>
        /// </summary>
        /// TODO test with Quaternion.RotateTowards
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
        
        public static float LerpAngle(float a, float b, float t)
        {
            float delta = Repeat((b - a), 360);
            if (delta > 180)
            {
                delta -= 360;
            }
            return a + delta * math.clamp(t, 0f, 1f);
        }

        public static float Repeat(float value, float max)
        {
            return (value - math.floor(value / max) * max);
        }
        
        /// <summary>
        ///   <para>Returns a copy of vector with its magnitude clamped to maxLength.</para>
        /// </summary>
        /// <param name="vector"></param>
        /// <param name="maxLength"></param>
        public static float2 ClampMagnitude(float2 vector, float maxLength)
        {
            float sqrMagnitude = math.lengthsq(vector);
            if ((double) sqrMagnitude <= (double) maxLength * (double) maxLength)
            {
                return vector;
            }
            float num1 = (float) math.sqrt((double) sqrMagnitude);
            float num2 = vector.x / num1;
            float num3 = vector.y / num1;
            return new float2(num2 * maxLength, num3 * maxLength);
        }
        
        /// <summary>
        ///   <para>Returns a copy of vector with its magnitude clamped to maxLength.</para>
        /// </summary>
        /// <param name="vector"></param>
        /// <param name="maxLength"></param>
        public static float3 ClampMagnitude(float3 vector, float maxLength)
        {
            float sqrMagnitude = math.lengthsq(vector);
            if ((double) sqrMagnitude <= (double) maxLength * (double) maxLength)
            {
                return vector;
            }
            float num1 = (float) math.sqrt((double) sqrMagnitude);
            float num2 = vector.x / num1;
            float num3 = vector.y / num1;
            float num4 = vector.z / num1;
            return new float3(num2 * maxLength, num3 * maxLength, num4 * maxLength);
        }
        
        /// <summary>
        ///   <para>Moves a point current towards target.</para>
        /// </summary>
        /// <param name="current"></param>
        /// <param name="target"></param>
        /// <param name="maxDistanceDelta"></param>
        public static float2 MoveTowards(float2 current, float2 target, float maxDistanceDelta)
        {
            float num1 = target.x - current.x;
            float num2 = target.y - current.y;
            float d = (float) ((double) num1 * (double) num1 + (double) num2 * (double) num2);
            if ((double) d == 0.0 || (double) maxDistanceDelta >= 0.0 && (double) d <= (double) maxDistanceDelta * (double) maxDistanceDelta)
            {
                return target;
            }
            float num3 = (float) math.sqrt((double) d);
            return new float2(current.x + num1 / num3 * maxDistanceDelta, current.y + num2 / num3 * maxDistanceDelta);
        }
        
        /// <summary>
        ///   <para>Calculates the angle between vectors from and.</para>
        /// </summary>
        /// <param name="from">The vector from which the angular difference is measured.</param>
        /// <param name="to">The vector to which the angular difference is measured.</param>
        /// <returns>
        ///   <para>The angle in degrees between the two vectors.</para>
        /// </returns>
        public static float VectorAngle(float3 from, float3 to)
        {
            float num = (float) math.sqrt((double) math.lengthsq(from) * (double) math.lengthsq(to));
            return (double) num < 1.0000000036274937E-15 ? 0.0f : (float) math.acos((double) math.clamp(math.dot(from, to) / num, -1f, 1f)) * 57.29578f;
        }
        
        /// <summary>
        ///   <para>Calculate a position between the points specified by current and target, moving no farther than the distance specified by maxDistanceDelta.</para>
        /// </summary>
        /// <param name="current">The position to move from.</param>
        /// <param name="target">The position to move towards.</param>
        /// <param name="maxDistanceDelta">Distance to move current per call.</param>
        /// <returns>
        ///   <para>The new position.</para>
        /// </returns>
        public static float3 MoveTowards(float3 current, float3 target, float maxDistanceDelta)
        {
            float num1 = target.x - current.x;
            float num2 = target.y - current.y;
            float num3 = target.z - current.z;
            float d = (float) ((double) num1 * (double) num1 + (double) num2 * (double) num2 + (double) num3 * (double) num3);
            if ((double) d == 0.0 || (double) maxDistanceDelta >= 0.0 && (double) d <= (double) maxDistanceDelta * (double) maxDistanceDelta)
            {
                return target;
            }
            float num4 = (float) math.sqrt((double) d);
            return new float3(current.x + num1 / num4 * maxDistanceDelta, current.y + num2 / num4 * maxDistanceDelta, current.z + num3 / num4 * maxDistanceDelta);
        }
        

        /// <summary>
        ///   <para>Rotates a vector current towards target.</para>
        /// </summary>
        /// <param name="current">The vector being managed.</param>
        /// <param name="target">The vector.</param>
        /// <param name="maxRadiansDelta">The maximum angle in radians allowed for this rotation.</param>
        /// <param name="maxMagnitudeDelta">The maximum allowed change in vector magnitude for this rotation.</param>
        /// <returns>
        ///   <para>The location that RotateTowards generates.</para>
        /// </returns>
        /// TODO test with Vector3.RotateTowards
        public static float3 RotateTowards(float3 current, float3 target, float maxRadiansDelta, float maxMagnitudeDelta)
        {
            // Avoid divide by zero
            if (math.lengthsq(current) == 0f || math.lengthsq(target) == 0f)
            {
                return current;
            }

            // Compute the current and target magnitudes
            float currentMagnitude = math.length(current);
            float targetMagnitude = math.length(target);

            // Normalize the current and target vectors
            float3 currentNorm = current / currentMagnitude;
            float3 targetNorm = target / targetMagnitude;

            // Find the angle between the current and target normalized vectors
            float dot = math.clamp(math.dot(currentNorm, targetNorm), -1.0f, 1.0f);
            float angle = math.acos(dot);

            // Rotate the current vector towards the target vector by the angle not greater than maxRadiansDelta
            float3 newDirection;
            if (angle < maxRadiansDelta)
            {
                newDirection = targetNorm;
            }
            else
            {
                float t = maxRadiansDelta / angle;
                newDirection = math.normalize(currentNorm + t * (targetNorm - currentNorm));
            }

            // Adjust the magnitude of the result vector by not exceeding maxMagnitudeDelta
            float newMagnitude = math.min(currentMagnitude + maxMagnitudeDelta, targetMagnitude);
            return newDirection * newMagnitude;
        }
    }
}