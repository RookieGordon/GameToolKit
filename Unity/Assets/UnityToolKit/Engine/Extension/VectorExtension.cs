/*
 * 功能描述：Vector4 扩展方法
 */

using UnityEngine;

namespace UnityToolKit.Engine.Extension
{
    public static class VectorExtension
    {
        /// <summary>
        /// 将 Vector4 转换为 Color
        /// </summary>
        public static Color ToColor(this Vector4 v)
        {
            return new Color(v.x, v.y, v.z, v.w);
        }

        /// <summary>
        /// 将 Vector3 转换为 Color
        /// </summary>
        public static Color ToColor(this Vector3 v)
        {
            return new Color(v.x, v.y, v.z, 1f);
        }

        /// <summary>
        /// 分量除法
        /// </summary>
        public static Vector3 Div(this Vector3 a, Vector3 b)
        {
            return new Vector3(
                b.x != 0 ? a.x / b.x : 0,
                b.y != 0 ? a.y / b.y : 0,
                b.z != 0 ? a.z / b.z : 0
            );
        }
    }
}
