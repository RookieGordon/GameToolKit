/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : 资源句柄的加载状态
 */

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 资源句柄加载状态
    /// </summary>
    public enum ELoadStatus
    {
        /// <summary> 尚未开始 </summary>
        None = 0,

        /// <summary> 加载中 </summary>
        Loading = 1,

        /// <summary> 加载成功 </summary>
        Succeed = 2,

        /// <summary> 加载失败 </summary>
        Failed = 3,

        /// <summary> 已取消 </summary>
        Cancelled = 4,

        /// <summary> 已卸载 (引用计数归零后释放) </summary>
        Unloaded = 5,
    }
}
