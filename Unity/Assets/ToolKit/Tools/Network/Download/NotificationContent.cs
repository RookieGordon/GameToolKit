/*
 * datetime     : 2026/2/20
 * description  : 通知栏内容数据
 */

namespace ToolKit.Tools.Network
{
    /// <summary>
    /// 通知栏内容
    /// </summary>
    public struct NotificationContent
    {
        /// <summary> 通知标题 </summary>
        public string Title;

        /// <summary> 通知正文 </summary>
        public string Body;

        /// <summary> 进度值 (0~1) </summary>
        public float Progress;

        public NotificationContent(string title, string body, float progress)
        {
            Title = title;
            Body = body;
            Progress = progress;
        }
    }
}
