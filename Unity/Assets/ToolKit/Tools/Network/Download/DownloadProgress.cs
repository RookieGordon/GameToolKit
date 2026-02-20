/*
 * datetime     : 2026/2/20
 * description  : 下载进度数据结构
 */

namespace ToolKit.Tools.Network
{
    /// <summary>
    /// 下载进度信息
    /// </summary>
    public struct DownloadProgress
    {
        /// <summary> 已下载字节数 </summary>
        public long BytesDownloaded;

        /// <summary> 总字节数, -1 表示未知 </summary>
        public long TotalBytes;

        /// <summary> 当前下载速度 (字节/秒) </summary>
        public double Speed;

        /// <summary> 下载进度比率 (0~1), 总大小未知时返回 -1 </summary>
        public float Ratio => TotalBytes > 0 ? (float)BytesDownloaded / TotalBytes : -1f;
    }
}
