/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 下载故障 → 资源两层错误的接入边界映射 (加载器组合处的一小段，不是公开服务)。
 *                网络模块不引用资源错误类型；这里的映射是加载器组合缓存与下载的胶水。
 *                日志脱敏：URL 只保留 scheme/host/path。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Network;

namespace ToolKit.Tools.Common
{
    public static class DownloadErrorMapping
    {
        /// <summary>
        /// 执行一次下载并把 DownloadException 映射为资源诊断码；
        /// 取消保持 OperationCanceledException 不包装；本地写盘故障不伪装为网络错误。
        /// </summary>
        public static async Task DownloadWithResourceErrorMappingAsync(
            Func<DownloadRequest, string, CancellationToken, Task> download,
            DownloadRequest request, string destinationPath, CancellationToken cancellationToken)
        {
            try
            {
                await download(request, destinationPath, cancellationToken).ConfigureAwait(false);
            }
            catch (DownloadException ex)
            {
                throw new ResourceLoadException(Map(ex, request));
            }
        }

        public static LoadError Map(DownloadException ex, DownloadRequest request)
        {
            var diagnosticCode = ex.ErrorKind switch
            {
                EDownloadError.InvalidUrl => DiagnosticCodes.NetworkInvalidUri,
                EDownloadError.Timeout => DiagnosticCodes.NetworkTimeout,
                EDownloadError.Network => ClassifyNetworkReason(ex),
                EDownloadError.Server when ex.HttpStatus is int status => HttpDiagnostic(status),
                EDownloadError.Server => DiagnosticCodes.NetworkHttpServerError,
                EDownloadError.NotFound => DiagnosticCodes.NetworkHttpNotFound,
                EDownloadError.AccessDenied => DiagnosticCodes.NetworkHttpDenied,
                EDownloadError.ServerBusy => DiagnosticCodes.NetworkHttpThrottled,
                EDownloadError.Storage => DiagnosticCodes.FileIoFailed,
                _ => DiagnosticCodes.InternalUnexpected,
            };
            if (ex.ErrorKind == EDownloadError.Storage)
            {
                // 下载器写入目标文件失败：存储故障，不能因来源是 HTTP 就伪装为网络问题
                diagnosticCode = FileSystemErrorClassifier.IsDiskFull(ex.InnerException ?? ex)
                    ? DiagnosticCodes.CacheDiskFull
                    : FileSystemErrorClassifier.IsAccessDenied(ex.InnerException ?? ex)
                        ? DiagnosticCodes.FileAccessDenied
                        : DiagnosticCodes.FileIoFailed;
            }

            return new LoadError(
                diagnosticCode, LoadStage.Download, CleanupStatus.Complete, ex.InnerException ?? ex,
                ContextOf(request, ex));
        }

        private static string ClassifyNetworkReason(DownloadException ex)
        {
            return ex.Reason switch
            {
                "域名解析失败" => DiagnosticCodes.NetworkDnsFailed,
                "TLS 握手失败" => DiagnosticCodes.NetworkTlsFailed,
                "连接建立失败" => DiagnosticCodes.NetworkConnectFailed,
                "连接中断" => DiagnosticCodes.NetworkInterrupted,
                _ => DiagnosticCodes.NetworkConnectFailed,
            };
        }

        private static string HttpDiagnostic(int status)
        {
            return status == 429
                ? DiagnosticCodes.NetworkHttpThrottled
                : status >= 500 && status <= 599
                    ? DiagnosticCodes.NetworkHttpServerError
                    : DiagnosticCodes.NetworkHttpUnexpected;
        }

        private static Dictionary<string, object> ContextOf(DownloadRequest request, DownloadException ex)
        {
            var url = request.Url;
            var context = new Dictionary<string, object>
            {
                // 日志脱敏：不输出请求头与带签名的完整 URL
                { "url", url.Scheme + "://" + url.Host + url.AbsolutePath },
                { "host", url.Host },
                { "downloadError", ex.ErrorKind.ToString() },
                { "reason", ex.Reason },
            };
            if (ex.HttpStatus is int status)
            {
                context["httpStatus"] = status;
            }
            return context;
        }
    }
}
