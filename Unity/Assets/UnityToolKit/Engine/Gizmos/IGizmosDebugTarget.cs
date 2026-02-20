/*
 * 功能描述：Gizmos调试可视化目标接口
 *           任何需要在Scene视图中进行调试可视化的对象均可实现此接口
 */

using UnityEngine;

namespace UnityToolKit.Engine.Gizmos
{
    /// <summary>
    /// Gizmos调试可视化目标接口
    /// <para>有形对象（具有Renderer）和无形对象（Trigger、AudioSource、Waypoint等）均可实现</para>
    /// </summary>
    public interface IGizmosDebugTarget
    {
        /// <summary>
        /// 调试显示名称，将作为3D标签显示在Scene视图中
        /// </summary>
        string DebugDisplayName { get; }

        /// <summary>
        /// 世界空间位置，用于标签放置和Gizmo绘制的锚点
        /// </summary>
        Vector3 DebugWorldPosition { get; }

        /// <summary>
        /// 是否为有形对象（具有Renderer等可视化渲染组件）
        /// <para>有形对象将显示包围盒线框，无形对象将显示占位Gizmo并支持点击交互</para>
        /// </summary>
        bool IsTangible { get; }

        /// <summary>
        /// 调试包围盒，用于绘制线框和计算标签偏移
        /// </summary>
        Bounds DebugBounds { get; }

        /// <summary>
        /// 详细调试信息，用户交互（点击）时在Scene视图或Inspector中展示
        /// </summary>
        string DebugDetailInfo { get; }

        /// <summary>
        /// 该目标是否仍然有效（未被销毁）
        /// </summary>
        bool IsAlive { get; }

        /// <summary>
        /// 当用户在Scene视图中点击该调试目标时触发
        /// </summary>
        void OnDebugInteract();

        /// <summary>
        /// 可选：自定义Gizmo绘制回调，在默认绘制之后调用
        /// </summary>
        void OnDrawDebugGizmos() { }
    }
}
