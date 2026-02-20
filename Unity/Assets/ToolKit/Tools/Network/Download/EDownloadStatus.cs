/*
 * datetime     : 2026/2/20
 * description  : 下载状态枚举定义
 */

namespace ToolKit.Tools.Network
{
    /// <summary>
    /// 下载任务状态
    /// </summary>
    public enum EDownloadStatus
    {
        /// <summary> 等待中 </summary>
        Pending,

        /// <summary> 下载中 </summary>
        Downloading,

        /// <summary> 已暂停 </summary>
        Paused,

        /// <summary> 已完成 </summary>
        Completed,

        /// <summary> 失败 </summary>
        Failed,

        /// <summary> 已取消 </summary>
        Cancelled
    }
}
