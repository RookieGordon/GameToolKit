/*
 * datetime     : 2026/2/20
 * description  : Android 平台下载处理器
 *                通过 AndroidJavaClass 调用 Java 端的 DownloadBridge,
 *                实现前台服务保活和通知栏显示
 *
 * 通知栏策略:
 *   - ShowNotification 配置控制是否允许显示通知
 *   - DisplayMode 控制显示时机 (Always / BackgroundOnly)
 *   - SmallIconName 自定义通知图标 (drawable 资源名)
 *   - 通知内容完全由业务层通过 ShowNotification 控制
 *
 * 后台策略:
 *   - 仅在 App 进入后台且有下载任务时启动前台服务 (dataSync 类型, 每天有时间限制)
 *   - 回到前台时立即停止前台服务
 */

#if UNITY_ANDROID

using UnityEngine;
using ToolKit.Tools.Network;

namespace UnityToolKit.Plugins.Download
{
    /// <summary>
    /// Android 平台后台下载处理器
    /// <para>通过前台服务 (Foreground Service) 保证 Unity 进入后台后下载不被系统中断</para>
    /// <para>后台服务仅在 App 进入后台且有下载任务时启动</para>
    /// <para>通知栏内容由业务层通过 ShowNotification 直接控制</para>
    /// </summary>
    public class AndroidDownloadHandler : MonoBehaviour, IPlatformDownloadHandler
    {
        #region Fields

        private static readonly string BridgeClassName = "com.toolkit.download.DownloadBridge";

        private AndroidJavaClass _bridge;
        private bool _serviceStarted;
        private bool _notificationVisible;
        private bool _initialized;
        private bool _isInBackground;
        private int _lastCompletedCount;
        private int _lastTotalCount;

        #endregion

        #region Properties

        /// <summary> Android 支持后台下载 (通过前台服务) </summary>
        public bool SupportsBackgroundDownload => true;

        /// <summary> 通知栏配置 </summary>
        public DownloadNotificationConfig NotificationConfig { get; set; }
            = new DownloadNotificationConfig();

        #endregion

        #region Public Methods

        /// <summary>
        /// 初始化桥接
        /// <para>必须在 Unity 主线程调用</para>
        /// </summary>
        public void Init()
        {
            if (_initialized) return;

            _bridge = new AndroidJavaClass(BridgeClassName);

            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                _bridge.CallStatic("init", activity);
            }

            _initialized = true;

            // 设置自定义图标
            if (!string.IsNullOrEmpty(NotificationConfig?.SmallIconName))
            {
                _bridge.CallStatic("setSmallIconName", NotificationConfig.SmallIconName);
            }

            // Android 13+ 需要运行时申请通知权限
            if (NotificationConfig?.ShowNotification != false)
            {
                RequestNotificationPermission();
            }
        }

        public void OnEnterBackground()
        {
            _isInBackground = true;

            // 仅在存在未完成的下载任务时才启动前台服务 (需求 2.1)
            // dataSync 类型服务每天有时间限制, 避免无任务时浪费配额
            if (!HasActiveTasks()) return;

            EnsureInitialized();

            if (!_serviceStarted)
            {
                // 启动服务时使用最小化通知 (业务层会通过 ShowNotification 更新内容)
                _bridge.CallStatic("startService", "下载中", "正在下载...");
                _serviceStarted = true;
            }
        }

        public void OnEnterForeground()
        {
            _isInBackground = false;

            // 停止后台服务
            if (_serviceStarted)
            {
                _bridge.CallStatic("stopService");
                _serviceStarted = false;
            }

            // BackgroundOnly 模式下, 回到前台时隐藏独立通知
            if (NotificationConfig?.DisplayMode == ENotificationDisplayMode.BackgroundOnly
                && _notificationVisible)
            {
                _bridge.CallStatic("hideNotification");
                _notificationVisible = false;
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

            EnsureInitialized();

            int percent = (int)(content.Progress * 100);
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            if (_serviceStarted)
            {
                // 后台: 更新服务通知
                _bridge.CallStatic("updateNotification",
                    content.Title, content.Body, percent);
            }
            else
            {
                // 前台 (DisplayMode.Always): 显示独立通知
                _bridge.CallStatic("showNotification",
                    content.Title, content.Body, percent);
                _notificationVisible = true;
            }
        }

        public void HideNotification()
        {
            if (_notificationVisible)
            {
                _bridge.CallStatic("hideNotification");
                _notificationVisible = false;
            }
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

        private void EnsureInitialized()
        {
            if (!_initialized) Init();
        }

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

        /// <summary>
        /// Android 13 (API 33)+ 动态申请 POST_NOTIFICATIONS 权限
        /// </summary>
        private void RequestNotificationPermission()
        {
#if UNITY_2023_1_OR_NEWER
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    "android.permission.POST_NOTIFICATIONS"))
            {
                UnityEngine.Android.Permission.RequestUserPermission(
                    "android.permission.POST_NOTIFICATIONS");
            }
#else
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var buildVersion = new AndroidJavaClass("android.os.Build$VERSION"))
            {
                int sdkInt = buildVersion.GetStatic<int>("SDK_INT");
                if (sdkInt >= 33)
                {
                    var permissions = new string[] { "android.permission.POST_NOTIFICATIONS" };
                    activity.Call("requestPermissions", permissions, 0);
                }
            }
#endif
        }

        #endregion
    }
}

#endif
