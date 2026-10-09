/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 内置 HTTP 传输 (P4)。流式读取响应正文 (ResponseHeadersRead)，不缓冲整文件；
 *                ConnectTimeout 用于建立连接阶段，ResponseTimeout 以每次读取的空闲预算实施；
 *                HTTP 状态码映射为 §11.2 的稳定诊断码；关闭隐式内容解压
 *                (需要解压的加载器应使用不同资源身份，§10.6)。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public sealed class HttpFileTransport : IFileTransport
    {
        private readonly NetworkOptions _options;
        private readonly object _clientGate = new object();
        private HttpClient? _redirectClient;
        private HttpClient? _noRedirectClient;
        private bool _disposed;

        public HttpFileTransport(NetworkOptions? options = null)
        {
            _options = options ?? new NetworkOptions();
            _options.Validate();
        }

        public async Task<TransportResponse> OpenReadAsync(FileRequest request, CancellationToken cancellationToken)
        {
            if (request.Source.Scheme != "http" && request.Source.Scheme != "https")
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.NetworkInvalidUri, LoadStage.Download, CleanupStatus.Complete, null,
                    _ContextOf(request, "scheme=" + request.Source.Scheme)));
            }

            var client = _GetClient(request.TransportContext.AllowRedirects, request.TransportContext.MaxRedirects);
            using var message = new HttpRequestMessage(HttpMethod.Get, request.Source);
            foreach (var header in request.TransportContext.Headers)
            {
                // 原始鉴权头只进入传输层，不进入磁盘索引与日志
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            message.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

            HttpResponseMessage response;
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCts.CancelAfter(_options.ConnectTimeout);
                try
                {
                    response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, connectCts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (!_IsCallerCancelled(ex, cancellationToken))
                {
                    throw new ResourceLoadException(_ClassifyConnectionError(ex, request));
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = _ClassifyStatus(response.StatusCode, request);
                response.Dispose();
                throw new ResourceLoadException(error);
            }

            var contentLength = response.Content.Headers.ContentLength;
            var etag = response.Headers.ETag?.ToString();
            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var timeoutStream = new IdleTimeoutStream(stream, _options.ResponseTimeout);

            return new TransportResponse(
                timeoutStream,
                contentLength,
                (int)response.StatusCode,
                etag,
                dispose: () =>
                {
                    timeoutStream.Dispose();
                    response.Dispose();
                });
        }

        public void Dispose()
        {
            lock (_clientGate)
            {
                _redirectClient?.Dispose();
                _noRedirectClient?.Dispose();
                _disposed = true;
            }
        }

        private HttpClient _GetClient(bool allowRedirects, int maxRedirects)
        {
            lock (_clientGate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(HttpFileTransport));
                }
                if (allowRedirects)
                {
                    if (_redirectClient == null)
                    {
                        var handler = new HttpClientHandler
                        {
                            AllowAutoRedirect = true,
                            MaxAutomaticRedirections = Math.Max(1, maxRedirects),
                            AutomaticDecompression = DecompressionMethods.None, // 关闭隐式解压
                        };
                        _redirectClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                    }
                    return _redirectClient;
                }
                if (_noRedirectClient == null)
                {
                    var handler = new HttpClientHandler
                    {
                        AllowAutoRedirect = false,
                        AutomaticDecompression = DecompressionMethods.None,
                    };
                    _noRedirectClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                }
                return _noRedirectClient;
            }
        }

        private static bool _IsCallerCancelled(Exception ex, CancellationToken callerToken)
        {
            return ex is OperationCanceledException && callerToken.IsCancellationRequested;
        }

        private LoadError _ClassifyConnectionError(Exception ex, FileRequest request)
        {
            var code = DiagnosticCodes.NetworkConnectFailed;
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is AuthenticationException)
                {
                    code = DiagnosticCodes.NetworkTlsFailed;
                    break;
                }
                if (e is SocketException socket)
                {
                    code = socket.SocketErrorCode == SocketError.HostNotFound
                        ? DiagnosticCodes.NetworkDnsFailed
                        : DiagnosticCodes.NetworkConnectFailed;
                    break;
                }
                if (e is OperationCanceledException)
                {
                    // 非调用者取消：连接/响应头阶段的传输超时
                    code = DiagnosticCodes.NetworkTimeout;
                    break;
                }
            }
            return new LoadError(code, LoadStage.Download, CleanupStatus.Complete, ex, _ContextOf(request, null));
        }

        private static LoadError _ClassifyStatus(HttpStatusCode statusCode, FileRequest request)
        {
            var code = (int)statusCode;
            string diagnostic = code == 404 || code == 410
                ? DiagnosticCodes.NetworkHttpNotFound
                : code == 401 || code == 403
                    ? DiagnosticCodes.NetworkHttpDenied
                    : code == 429
                        ? DiagnosticCodes.NetworkHttpThrottled
                        : code >= 500 && code <= 599
                            ? DiagnosticCodes.NetworkHttpServerError
                            : DiagnosticCodes.NetworkHttpUnexpected;
            return new LoadError(diagnostic, LoadStage.Download, CleanupStatus.Complete, null,
                _ContextOf(request, "http_status=" + code));
        }

        private static Dictionary<string, object> _ContextOf(FileRequest request, string? fact)
        {
            var uri = request.Source;
            // 日志脱敏：只保留 scheme/host/path
            var sanitized = uri.Scheme + "://" + uri.Host + uri.AbsolutePath;
            var context = new Dictionary<string, object>
            {
                { "url", sanitized },
                { "host", uri.Host },
                { "identity", request.Identity.ToString() },
            };
            if (fact != null)
            {
                context["fact"] = fact;
            }
            return context;
        }

        /// <summary> 以空闲预算包装读取：单次读超过 ResponseTimeout 未返回即超时中断 </summary>
        private sealed class IdleTimeoutStream : Stream
        {
            private readonly Stream _inner;
            private readonly TimeSpan _idleTimeout;

            public IdleTimeoutStream(Stream inner, TimeSpan idleTimeout)
            {
                _inner = inner;
                _idleTimeout = idleTimeout;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_idleTimeout);
                try
                {
                    return await _inner.ReadAsync(buffer, offset, count, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.NetworkTimeout, LoadStage.Download, CleanupStatus.Complete));
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException("TransportResponse.Body 只前进异步读取");
            }

            public override void Flush()
            {
                throw new NotSupportedException();
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
