/*
 * 功能描述：InspectorButton特性，允许在Inspector中显示方法调用按钮
 *           借鉴自 Unity3D-ToolChain_StriteR
 */

using System;

namespace UnityToolKit.Engine
{
    /// <summary>
    /// 标记在公共方法上，在Editor Inspector中生成调用按钮
    /// 使用时写 [InspectorButton] 即可
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class InspectorButtonAttribute : Attribute
    {
    }
}
