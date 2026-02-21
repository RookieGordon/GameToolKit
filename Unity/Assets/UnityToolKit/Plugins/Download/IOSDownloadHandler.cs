/*
 * datetime     : 2026/2/20
 * description  : iOS 平台下载处理器
 *                通过 P/Invoke 调用 Objective-C 端的 DownloadBridge,
 *                实现后台任务延长和本地通知显示
 *
 * 通知栏策略:
 *   - ShowNotification 配置控制是否发送本地通知 (iOS 后台任务不要求通知, 可完全关闭)
 *   - DisplayMode 控制显示时机 (Always / BackgroundOnly)
 *   - 通知内容完全由业务层通过 ShowNotification 控制
 *
 * 后台策略:
 *   - 仅在 App 进入后台且有下载任务时申请后台时间
 *   - 通过 beginBackgroundTask 延长后台执行 (约 30 秒~3 分钟)
 *   - 到期时自动尝试重新申请, 尽量延长后台时间
 */

#if UNITY_IOS

using System.Runtime.InteropServices;
using ToolKit.Tools.Network;
using UnityEngine;

namespace UnityToolKit.Plugins.Download
{
    /// <summary>
    /// iOS 平台后台下载处理器
    /// <para>通过 beginBackgroundTask 延长后台执行时间, 支持自动续期</para>
    /// <para>通知栏内容由业务层通过 ShowNotification 直接控制</para>
    /// </summary>
    public class IOSDownloadHandler : MonoBehaviour, IPlatformDownloadHandler
    {
        #region Native Methods

        [DllImport("__Internal")]
        private static extern void ToolKit_Download_RequestNotificationPermission();

        [DllImport("__Internal")]
        private static extern void ToolKit_Download_BeginBackgroundTask();

        [DllImport("__Internal")]
        private static extern void ToolKit_Download_EndBackgroundTask();

        [DllImport("__Internal")]
        private static extern void ToolKit_Download_ShowNotification(
            string title, string content, float progress);

        [DllImport("__Internal")]
        private static extern void ToolKit_Download_HideNotification();

        [DllImport("__Internal")]
        private static extern void ToolKit_Download_EnableForegroundNotification();

        #endregion

        #region Fields

        private bool _backgroundTaskActive;
        private bool _isInBackground;
        private bool _permissionRequested;
        private int _lastCompletedCount;
        private int _lastTotalCount;

        #endregion

        #region Properties

        /// <summary>
        /// iOS 有限支持后台下载
        /// <para>通过 beginBackgroundTask 可延长约 30 秒~3 分钟, 支持自动续期</para>
        /// </summary>
        public bool SupportsBackgroundDownload => true;

        /// <summary> 通知栏配置 </summary>
        public DownloadNotificationConfig NotificationConfig { get; set; }
            = new DownloadNotificationConfig();

        #endregion

        #region Public Methods

        /// <summary>
        /// 初始化, 根据配置申请通知权限
        /// </summary>
        public void Init()
        {
            if (NotificationConfig?.ShowNotification != false)
            {
                EnsurePermission();
            }

            // Always 模式下启用前台通知显示
            // (默认 iOS 前台时不展示本地通知, 需设置 UNUserNotificationCenterDelegate)
            if (NotificationConfig?.DisplayMode == ENotificationDisplayMode.Always)
            {
                ToolKit_Download_EnableForegroundNotification();
            }
        }

        public void OnEnterBackground()
        {
            _isInBackground = true;

            // 仅在存在未完成的下载任务时才申请后台时间 (需求 2.1)
            if (!HasActiveTasks()) return;

            if (!_backgroundTaskActive)
            {
                ToolKit_Download_BeginBackgroundTask();
                _backgroundTaskActive = true;
            }
        }

        public void OnEnterForeground()
        {
            _isInBackground = false;

            if (_backgroundTaskActive)
            {
                ToolKit_Download_EndBackgroundTask();
                _backgroundTaskActive = false;
            }

            // BackgroundOnly 模式下, 回到前台时隐藏通知
            if (NotificationConfig?.DisplayMode == ENotificationDisplayMode.BackgroundOnly)
            {
                ToolKit_Download_HideNotification();
            }
        }

        public void UpdateActiveTaskCount(int completedCount, int totalCount)
        {
            _lastCompletedCount = completedCount;
            _lastTotalCount = totalCount;
        }

        public void ShowNotification(NotificationContent content)
        {
            if (!ShouldShowNotification()) return;

            EnsurePermission();
            ToolKit_Download_ShowNotification(content.Title, content.Body, content.Progress);
        }

        public void HideNotification()
        {
            ToolKit_Download_HideNotification();
        }

        #endregion

        #region Unity Callbacks

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
                OnEnterBackground();
            else
                OnEnterForeground();
        }

        #endregion

        #region Private Methods

        /// <summary> 是否存在未完成的下载任务 </summary>
        private bool HasActiveTasks()
        {
            return _lastTotalCount > 0 && _lastCompletedCount < _lastTotalCount;
        }

        /// <summary> 根据配置判断当前是否应该显示通知 </summary>
        private bool ShouldShowNotification()
        {
            var config = NotificationConfig;
            if (config == null || !config.ShowNotification) return false;

            if (config.DisplayMode == ENotificationDisplayMode.BackgroundOnly
                && !_isInBackground)
                return false;

            return true;
        }

        private void EnsurePermission()
        {
            if (!_permissionRequested)
            {
                _permissionRequested = true;
                ToolKit_Download_RequestNotificationPermission();
            }
        }

        #endregion
    }
}

#endif
