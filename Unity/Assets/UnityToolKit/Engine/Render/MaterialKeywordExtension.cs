/*
 * 功能描述：Material Shader关键字扩展方法
 *           借鉴自 Unity3D-ToolChain_StriteR
 */

using System;
using UnityEngine;

namespace UnityToolKit.Engine.Render
{
    public static class MaterialKeywordExtension
    {
        /// <summary>
        /// 根据枚举值启用对应的Shader关键字，禁用同枚举中的其他关键字
        /// 枚举名称即为Shader关键字名称
        /// </summary>
        public static void EnableKeywords<T>(this Material material, T keyword) where T : Enum
        {
            var values = Enum.GetValues(typeof(T));
            var targetName = keyword.ToString();

            foreach (var value in values)
            {
                var name = value.ToString();
                if (name == targetName)
                {
                    material.EnableKeyword(name);
                }
                else
                {
                    material.DisableKeyword(name);
                }
            }
        }
    }
}
