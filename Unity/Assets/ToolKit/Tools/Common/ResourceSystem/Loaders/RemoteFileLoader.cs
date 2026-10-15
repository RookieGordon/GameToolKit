/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 内置远端文件加载器。组合文件缓存 (GetOrCreateAsync + fill) 与注入的下载委托，
 *                并把下载故障映射为资源诊断码。下载重试、并发与超时都在下载实现内部，一次共享填充只调用一次 fill。
 *                内容是否更新由身份里的 revision 表达；有效期是"业务认为这份内容最多新到什么时候"，
 *                每次请求独立给出，不在加载器构造时算一次，也不替代磁盘删除策略。
 *                目标类型与转换参数进入资源身份，多种内存表示复用同一次下载。
 */

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Network;

namespace ToolKit.Tools.Common
{
    public sealed class RemoteFileLoader : IResourceLoader
    {
        /// <summary> 无明确版本动态 URL 的默认有效期；内容更新由业务切换版本身份或缩短 TTL </summary>
        public static readonly TimeSpan DefaultDynamicUrlTtl = TimeSpan.FromHours(24);

        private readonly FileCache _fileCache;
        private readonly FileResourceConverters _converters;
        private readonly Func<DownloadRequest, string, CancellationToken, Task> _download;
        private readonly Func<string, RemoteFileRequest>? _requestBuilder;
        private readonly TimeSpan? _defaultTtl;

        /// <param name="download">下载委托，例如 downloader.DownloadAsync；缓存不知道它如何取得内容</param>
        /// <param name="requestBuilder">业务注入的 URL → RemoteFileRequest 映射：提供稳定身份与期限时签名 URL 可合并</param>
        /// <param name="defaultTtl">默认请求 (无 builder) 的有效期；null 表示不过期</param>
        public RemoteFileLoader(
            FileCache fileCache,
            Func<DownloadRequest, string, CancellationToken, Task> download,
            FileResourceConverters converters,
            Func<string, RemoteFileRequest>? requestBuilder = null,
            TimeSpan? defaultTtl = null)
        {
            _fileCache = fileCache ?? throw new ArgumentNullException(nameof(fileCache));
            _download = download ?? throw new ArgumentNullException(nameof(download));
            _converters = converters ?? throw new ArgumentNullException(nameof(converters));
            _requestBuilder = requestBuilder;
            _defaultTtl = defaultTtl ?? DefaultDynamicUrlTtl;
        }

        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(request.Address, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.NetworkInvalidUri, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "address", request.Address } }));
            }

            var remoteRequest = _requestBuilder != null ? _requestBuilder(request.Address) : _DefaultRequest(uri);
            var conversionKey = FileLoadParameters.GetConversionKey(request.Parameters);
            var converter = _converters.Get(request.RequestedType);

            var localKey = string.Join("|",
                "remote", _IdentityMaterial(remoteRequest.Cache.Identity),
                LocalFileLoader._TypeKey(request.RequestedType), conversionKey ?? "");
            return Task.FromResult(new ResolvedResource(localKey, request.RequestedType,
                new RemotePayload(remoteRequest, converter, conversionKey)));
        }

        public async Task<LoadedAsset> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            var payload = (RemotePayload)resource.Payload!;
            var remoteRequest = payload.Request;

            // 组合缓存与下载：fill 先把下载故障映射为资源错误，再写入缓存分配的临时路径。
            var path = await _fileCache.GetOrCreateAsync(
                remoteRequest.Cache,
                (temporaryPath, cacheToken) => DownloadErrorMapping.DownloadWithResourceErrorMappingAsync(
                    _download, remoteRequest.Download, temporaryPath, cacheToken),
                operationToken).ConfigureAwait(false);

            try
            {
                // 转换失败不自动证明下载文件损坏，不删除也不失效缓存文件。
                return await payload.Converter
                    .ConvertAsync(path, resource.RepresentationType, payload.ConversionKey, operationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not ResourceLoadException and not OperationCanceledException)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetConversionFailed, LoadStage.Convert, CleanupStatus.Unknown, error,
                    new Dictionary<string, object> { { "url", _Sanitize(remoteRequest.Download.Url) } }));
            }
        }

        /// <summary>
        /// 默认身份：保留影响资源内容的完整 URL —— 端口与查询参数都参与身份。
        /// revision 为完整 URL 的摘要，有效性为默认 TTL：不默认永久信任同 URL 的内容。
        /// </summary>
        private RemoteFileRequest _DefaultRequest(Uri uri)
        {
            var identity = new FileIdentity(
                "remote",
                uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port) + uri.AbsolutePath,
                _UrlRevision(uri),
                "");
            var validity = _defaultTtl is TimeSpan ttl ? FileValidity.ExpiresAfter(ttl) : FileValidity.Forever;
            return new RemoteFileRequest(identity, validity, uri);
        }

        private static string _UrlRevision(Uri uri)
        {
            var fullUrl = uri.Scheme + "://" + uri.Host
                          + (uri.IsDefaultPort ? "" : ":" + uri.Port)
                          + uri.PathAndQuery;
            using var sha = SHA256.Create();
            return _ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(fullUrl)));
        }

        /// <summary> 身份文本进入资源标识：逐字段长度前缀，长度与内容都保留，不做大小写归一 </summary>
        private static string _IdentityMaterial(FileIdentity identity)
        {
            var builder = new StringBuilder();
            _AppendField(builder, identity.Source);
            _AppendField(builder, identity.Path);
            _AppendField(builder, identity.Revision);
            _AppendField(builder, identity.Variant);
            return builder.ToString();
        }

        private static void _AppendField(StringBuilder builder, string value)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('\n');
        }

        private static string _Sanitize(Uri uri) => uri.Scheme + "://" + uri.Host + uri.AbsolutePath;

        private static string _ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        private sealed class RemotePayload
        {
            internal RemotePayload(RemoteFileRequest request, IFileResourceConverter converter, string? conversionKey)
            {
                Request = request;
                Converter = converter;
                ConversionKey = conversionKey;
            }

            internal RemoteFileRequest Request { get; }

            internal IFileResourceConverter Converter { get; }

            internal string? ConversionKey { get; }
        }
    }

    /// <summary>
    /// 下载故障 → 资源错误的映射边界。网络模块不引用资源错误类型；
    /// 这里只做分类与脱敏，不重试、不推断协议。取消保持 OperationCanceledException
    /// 不包装；写盘失败不因来源是 HTTP 就伪装成网络问题。
    /// </summary>
    internal static class DownloadErrorMapping
    {
        /// <summary> 执行一次下载并把 DownloadException 映射为资源诊断码 </summary>
        internal static async Task DownloadWithResourceErrorMappingAsync(
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

        internal static LoadError Map(DownloadException error, DownloadRequest request)
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
                diagnosticCode, LoadStage.Download, CleanupStatus.Complete,
                error.InnerException ?? error, _ContextOf(request, error));
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
