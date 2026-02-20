/*
 * datetime     : 2026/2/20
 * description  : 平台下载处理器工厂
 *                根据当前运行平台自动创建对应的 IPlatformDownloadHandler 实现
 */

using ToolKit.Tools.Network;
using UnityEngine;

namespace UnityToolKit.Engine.EnginePlugins
{
    /// <summary>
    /// 平台下载处理器工厂
    /// <para>根据运行平台自动创建对应的平台下载处理器</para>
    /// </summary>
    public static class PlatformDownloadHandlerFactory
    {
        /// <summary>
        /// 创建当前平台对应的下载处理器
        /// </summary>
        /// <param name="config">通知栏配置, 为 null 时使用默认配置</param>
        /// <returns>平台处理器实例, 不支持的平台返回 null</returns>
        public static IPlatformDownloadHandler Create(DownloadNotificationConfig config = null)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var go = new GameObject("[DownloadHandler]");
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            var handler = go.AddComponent<AndroidDownloadHandler>();
            handler.NotificationConfig = config ?? new DownloadNotificationConfig();
            handler.Init();
            return handler;
#elif UNITY_IOS && !UNITY_EDITOR
            var go = new GameObject("[DownloadHandler]");
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            var handler = go.AddComponent<IOSDownloadHandler>();
            handler.NotificationConfig = config ?? new DownloadNotificationConfig();
            handler.Init();
            return handler;
#else
            return null;
#endif
        }
    }
}
