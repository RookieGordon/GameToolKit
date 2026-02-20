/*
 * datetime     : 2026/2/20
 * description  : 下载错误类型枚举
 */

namespace ToolKit.Tools.Network
{
    /// <summary>
    /// 下载错误类型
    /// <para>业务层只需关注错误分类, 具体异常信息输出到日志</para>
    /// </summary>
    public enum EDownloadError
    {
        /// <summary> 未知错误 (未归类的异常) </summary>
        Unknown,

        /// <summary> 网络错误 (连接超时、DNS 解析失败、网络不可达等) </summary>
        Network,

        /// <summary> 服务器错误 (HTTP 4xx/5xx 响应) </summary>
        Server,

        /// <summary> 请求地址无效 (URL 格式错误、协议不支持) </summary>
        InvalidUrl,

        /// <summary> 存储错误 (磁盘已满、权限不足、路径无效等) </summary>
        Storage,

        /// <summary> 操作已取消 </summary>
        Cancelled,

        /// <summary> 操作超时 (连接超时或读取超时) </summary>
        Timeout
    }
}
