/*
 * datetime     : 2026/2/20
 * description  : 通知栏显示时机枚举
 */

namespace ToolKit.Tools.Network
{
    /// <summary>
    /// 通知栏显示时机
    /// </summary>
    public enum ENotificationDisplayMode
    {
        /// <summary> 有下载任务时始终显示通知栏 (包括前台) </summary>
        Always,

        /// <summary> 仅在 App 进入后台时显示通知栏 </summary>
        BackgroundOnly
    }
}
