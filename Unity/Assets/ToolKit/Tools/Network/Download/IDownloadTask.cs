/*
 * datetime     : 2026/2/20
 * description  : 下载任务接口定义
 */

namespace ToolKit.Tools.Network
{
    /// <summary>
    /// 下载任务接口
    /// </summary>
    public interface IDownloadTask
    {
        /// <summary>
        /// 任务标签, 用于业务层定位具体任务
        /// <para>未指定时默认为下载文件名</para>
        /// </summary>
        string Tag { get; }

        /// <summary> 下载地址 </summary>
        string Url { get; }

        /// <summary> 保存路径 </summary>
        string SavePath { get; }

        /// <summary> 当前状态 </summary>
        EDownloadStatus Status { get; }

        /// <summary> 当前下载进度 </summary>
        DownloadProgress Progress { get; }

        /// <summary> 暂停下载 </summary>
        void Pause();

        /// <summary> 取消下载 </summary>
        void Cancel();
    }
}
