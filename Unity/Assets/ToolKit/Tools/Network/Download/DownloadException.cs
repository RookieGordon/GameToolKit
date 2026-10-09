/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 下载模块的结构化故障：保留粗粒度 EDownloadError，并携带可排查原因、
 *                可选 HTTP 状态与原始异常。不引用资源系统错误类型。
 */

using System;

namespace ToolKit.Tools.Network
{
    public sealed class DownloadException : Exception
    {
        public EDownloadError ErrorKind { get; }
        public string Reason { get; }
        /// <summary> HTTP 状态码 (协议错误时) </summary>
        public int? HttpStatus { get; }

        public DownloadException(EDownloadError errorKind, string reason,
            int? httpStatus = null, Exception? inner = null)
            : base(BuildMessage(errorKind, reason, httpStatus), inner)
        {
            ErrorKind = errorKind;
            Reason = reason;
            HttpStatus = httpStatus;
        }

        private static string BuildMessage(EDownloadError kind, string reason, int? status)
        {
            return status != null ? $"[{kind}] {reason} (HTTP {status})" : $"[{kind}] {reason}";
        }
    }
}
