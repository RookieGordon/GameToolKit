/*
 * 功能描述：Color工具类
 */

using UnityEngine;

namespace UnityToolKit.Runtime.Utility
{
    public static class ColorUtil
    {
        /// <summary>
        /// 将 Vector3 转换为 Color (用于将顶点位置/法向量写入纹理)
        /// </summary>
        public static Color ToColor(Vector3 v)
        {
            return new Color(v.x, v.y, v.z, 1f);
        }

        /// <summary>
        /// 将 Vector4 转换为 Color
        /// </summary>
        public static Color ToColor(Vector4 v)
        {
            return new Color(v.x, v.y, v.z, v.w);
        }
    }
}
