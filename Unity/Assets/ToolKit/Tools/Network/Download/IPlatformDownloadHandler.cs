/*
 * datetime     : 2026/2/20
 * description  : 平台下载处理接口 (后台下载、通知栏等)
 */

namespace ToolKit.Tools.Network
{
    /// <summary>
    /// 平台相关下载处理接口
    /// <para>移动平台的后台下载和通知栏显示功能</para>
    /// <para>后台服务仅在 App 进入后台且有下载任务时启用</para>
    /// <para>通知栏内容完全由业务层通过 ShowNotification/HideNotification 控制</para>
    /// <para>具体实现在 UnityToolKit/EnginePlugins/Download 下面</para>
    /// </summary>
    public interface IPlatformDownloadHandler
    {
        /// <summary> 是否支持后台下载 </summary>
        bool SupportsBackgroundDownload { get; }

        /// <summary>
        /// 通知栏配置
        /// <para>控制通知栏的显示开关、显示时机和图标</para>
        /// </summary>
        DownloadNotificationConfig NotificationConfig { get; set; }

        /// <summary>
        /// App 进入后台时调用
        /// <para>有下载任务时启动后台服务</para>
        /// </summary>
        void OnEnterBackground();

        /// <summary>
        /// App 回到前台时调用
        /// <para>停止后台服务; BackgroundOnly 模式下自动隐藏通知</para>
        /// </summary>
        void OnEnterForeground();

        /// <summary>
        /// 更新活跃任务数 (供后台服务判断是否需要启动)
        /// <para>由下载器在任务完成时调用, 不涉及通知显示</para>
        /// </summary>
        /// <param name="completedCount">已完成的任务数</param>
        /// <param name="totalCount">总任务数</param>
        void UpdateActiveTaskCount(int completedCount, int totalCount);

        /// <summary>
        /// 显示/更新通知栏
        /// <para>由业务层调用, 内容完全由业务层决定</para>
        /// <para>受 <see cref="DownloadNotificationConfig.ShowNotification"/> 和
        /// <see cref="DownloadNotificationConfig.DisplayMode"/> 约束</para>
        /// </summary>
        /// <param name="content">通知内容</param>
        void ShowNotification(NotificationContent content);

        /// <summary>
        /// 隐藏通知栏
        /// </summary>
        void HideNotification();
    }
}
