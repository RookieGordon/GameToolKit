/*
 * datetime     : 2026/2/20
 * description  : 平台下载通知配置
 */

namespace ToolKit.Tools.Network
{
    /// <summary>
    /// 平台下载通知配置
    /// <para>控制通知栏的显示开关、显示时机和图标</para>
    /// <para>通知内容由业务层通过 <see cref="IPlatformDownloadHandler.ShowNotification"/> 直接控制</para>
    /// </summary>
    public class DownloadNotificationConfig
    {
        /// <summary>
        /// 是否显示通知栏
        /// <para>Android: 前台服务要求显示通知, 此选项为 false 时通知以最低优先级展示</para>
        /// <para>iOS: 后台任务不要求通知, 此选项为 false 时不发送本地通知</para>
        /// </summary>
        public bool ShowNotification { get; set; } = true;

        /// <summary>
        /// 通知栏显示时机
        /// <para>Always: 有下载任务时始终显示 (包括前台)</para>
        /// <para>BackgroundOnly: 仅在 App 进入后台时显示 (默认)</para>
        /// </summary>
        public ENotificationDisplayMode DisplayMode { get; set; } = ENotificationDisplayMode.BackgroundOnly;

        /// <summary>
        /// 通知小图标资源名
        /// <para>Android: drawable 资源名 (如 "ic_download"), 为 null 时使用系统默认图标</para>
        /// <para>iOS: 不适用, 始终使用 App 图标</para>
        /// </summary>
        public string SmallIconName { get; set; }
    }
}
