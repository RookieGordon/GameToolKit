/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 下载故障 → 资源两层错误的映射边界。网络模块不引用资源错误类型；
 *                这里只做分类与脱敏，不重试、不推断协议。取消保持 OperationCanceledException
 *                不包装；写盘失败不因来源是 HTTP 就伪装成网络问题。
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
        /// <summary> 执行一次下载并把 DownloadException 映射为资源诊断码 </summary>
        public static async Task DownloadWithResourceErrorMappingAsync(
            Func<DownloadRequest, string, CancellationToken, Task> download,
            DownloadRequest request, string destinationPath, CancellationToken cancellationToken)
        {
            try
            {
                await download(request, destinationPath, cancellationToken).ConfigureAwait(false);
            }
            catch (DownloadException error) when (error.ErrorKind != EDownloadError.Cancelled)
            {
                throw new ResourceLoadException(Map(error, request));
            }
        }

        public static LoadError Map(DownloadException error, DownloadRequest request)
        {
            var diagnosticCode = error.ErrorKind switch
            {
                EDownloadError.InvalidUrl => DiagnosticCodes.NetworkInvalidUri,
                EDownloadError.Timeout => DiagnosticCodes.NetworkTimeout,
                EDownloadError.Network => _ClassifyNetworkReason(error),
                EDownloadError.Server when error.HttpStatus is int status => _HttpDiagnostic(status),
                EDownloadError.Server => DiagnosticCodes.NetworkHttpServerError,
                EDownloadError.NotFound => DiagnosticCodes.NetworkHttpNotFound,
                EDownloadError.AccessDenied => DiagnosticCodes.NetworkHttpDenied,
                EDownloadError.ServerBusy => DiagnosticCodes.NetworkHttpThrottled,
                EDownloadError.Storage => _ClassifyStorage(error),
                _ => DiagnosticCodes.InternalUnexpected,
            };

            return new LoadError(
                diagnosticCode, LoadStage.Download, CleanupStatus.Complete, error.InnerException ?? error,
                _ContextOf(request, error));
        }

        private static string _ClassifyNetworkReason(DownloadException error)
        {
            return error.Reason switch
            {
                "域名解析失败" => DiagnosticCodes.NetworkDnsFailed,
                "TLS 握手失败" => DiagnosticCodes.NetworkTlsFailed,
                "连接建立失败" => DiagnosticCodes.NetworkConnectFailed,
                "连接中断" => DiagnosticCodes.NetworkInterrupted,
                _ => DiagnosticCodes.NetworkConnectFailed,
            };
        }

        private static string _ClassifyStorage(DownloadException error)
        {
            var inner = error.InnerException ?? error;
            return _IsAccessDenied(inner) ? DiagnosticCodes.FileAccessDenied : DiagnosticCodes.FileIoFailed;
        }

        private static bool _IsAccessDenied(Exception error) =>
            error is UnauthorizedAccessException
            || error.HResult == unchecked((int)0x80070005);

        private static string _HttpDiagnostic(int status)
        {
            return status == 429
                ? DiagnosticCodes.NetworkHttpThrottled
                : status >= 500 && status <= 599
                    ? DiagnosticCodes.NetworkHttpServerError
                    : DiagnosticCodes.NetworkHttpUnexpected;
        }

        private static Dictionary<string, object> _ContextOf(DownloadRequest request, DownloadException error)
        {
            var url = request.Url;
            var context = new Dictionary<string, object>
            {
                // 日志脱敏：不输出请求头与带签名的完整 URL
                { "url", url.Scheme + "://" + url.Host + url.AbsolutePath },
                { "host", url.Host },
                { "downloadError", error.ErrorKind.ToString() },
                { "reason", error.Reason },
            };
            if (error.HttpStatus is int status) context["httpStatus"] = status;
            return context;
        }
    }
}
