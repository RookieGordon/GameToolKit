/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 内置远端文件加载器 (HTTP 解耦版)。组合纯缓存 (GetOrCreateAsync + fill) 与注入的
 *                下载委托 (默认由装配根传入 SimpleDownloader.DownloadAsync)；下载重试/并发/超时
 *                全部在下载模块内，一次共享填充只调用一次 fill。
 *                默认身份保留内容相关的端口/查询；默认请求带 TTL，不永久信任无版本 URL；
 *                只有业务 builder 给出稳定身份时才允许不同签名 URL 共用文件。
 *                decoderId/目标表示/解码参数进入 LocalKey，多种内存表示复用同一下载文件。
 *                取消后的迟到结果不交付；解码失败不自动证明下载文件损坏。
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
        private readonly DecoderRegistry _decoders;
        private readonly Func<DownloadRequest, string, CancellationToken, Task> _download;
        private readonly Func<string, RemoteFileRequest>? _requestBuilder;
        private readonly FileValidity _defaultValidity;

        /// <param name="download">下载委托，默认由装配根传入 downloader.DownloadAsync；缓存不知道它如何取得内容</param>
        /// <param name="requestBuilder">业务注入的 URL → RemoteFileRequest 映射：提供稳定身份时签名 URL 可合并</param>
        /// <param name="defaultValidity">默认请求 (无 builder) 的有效期；默认 24h TTL</param>
        public RemoteFileLoader(
            FileCache fileCache,
            Func<DownloadRequest, string, CancellationToken, Task> download,
            DecoderRegistry decoders,
            Func<string, RemoteFileRequest>? requestBuilder = null,
            FileValidity? defaultValidity = null)
        {
            _fileCache = fileCache ?? throw new ArgumentNullException(nameof(fileCache));
            _download = download ?? throw new ArgumentNullException(nameof(download));
            _decoders = decoders ?? throw new ArgumentNullException(nameof(decoders));
            _requestBuilder = requestBuilder;
            _defaultValidity = defaultValidity ?? FileValidity.ExpiresAfter(DefaultDynamicUrlTtl);
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

            var remoteRequest = _requestBuilder != null
                ? _requestBuilder(request.Address)
                : _DefaultRequest(uri);
            var parameters = FileLoadParameters.From(request.Parameters);
            var decoder = _decoders.Resolve(parameters.DecoderId, request.RequestedType);

            // 文件身份 + 解码表示 + 解码参数构成资源身份；同一下载文件可支撑多种内存表示
            var localKey = string.Join("|",
                "remote", FileKeyEncoding.Encode(remoteRequest.Cache.Identity), decoder.Id,
                request.RequestedType.FullName ?? request.RequestedType.Name, parameters.DecodeKey ?? "");
            return Task.FromResult(new ResolvedResource(localKey, request.RequestedType,
                new RemotePayload(remoteRequest, decoder.Id, parameters.DecodeKey)));
        }

        public async Task<LoadedAsset> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            var payload = (RemotePayload)resource.Payload!;
            var decoder = _decoders.Resolve(payload.DecoderId, resource.RepresentationType);
            var remoteRequest = payload.Request;

            // 组合缓存与下载：fill 把下载故障映射为资源错误后写入缓存分配的临时路径
            string path;
            try
            {
                path = await _fileCache.GetOrCreateAsync(
                    remoteRequest.Cache,
                    (temporaryPath, cacheToken) => DownloadErrorMapping.DownloadWithResourceErrorMappingAsync(
                        _download, remoteRequest.Download, temporaryPath, cacheToken),
                    operationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ResourceLoadException)
            {
                throw; // 下载/校验失败保留映射后的错误
            }

            try
            {
                // 成功解码：真实 LoadedAsset 转交框架，释放操作由解码器结果自带；
                // 解码失败不自动证明下载文件损坏：不删除文件、不失效缓存
                return await decoder.DecodeAsync(path, resource.RepresentationType, payload.DecodeKey, operationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // 解码取消：解码器契约保证回退自身中间对象
            }
            catch (ResourceLoadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 未分类异常：无法确认解码器中间对象是否清理，不猜测
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetDecodeFailed, LoadStage.Decode, CleanupStatus.Unknown, ex,
                    new Dictionary<string, object> { { "url", _Sanitize(remoteRequest.Download.Url) } }));
            }
        }

        /// <summary>
        /// 默认身份：保留影响资源内容的完整 URL —— 端口与查询参数都参与身份
        /// (查询不同视为不同内容)。revision 为完整 URL 的摘要，有效性为默认 TTL：
        /// 服务器替换同 URL 内容后到期重新下载，不默认永久信任。
        /// </summary>
        private RemoteFileRequest _DefaultRequest(Uri uri)
        {
            var fullUrl = uri.Scheme + "://" + uri.Host
                          + (uri.IsDefaultPort ? "" : ":" + uri.Port)
                          + uri.PathAndQuery;
            using var sha = SHA256.Create();
            var revision = _ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(fullUrl)));
            var identity = new FileIdentity(
                "remote",
                uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port) + uri.AbsolutePath,
                revision,
                "");
            return new RemoteFileRequest(identity, _defaultValidity, uri);
        }

        private static string _Sanitize(Uri uri)
        {
            return uri.Scheme + "://" + uri.Host + uri.AbsolutePath;
        }

        private static string _ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }

        private sealed class RemotePayload
        {
            public readonly RemoteFileRequest Request;
            public readonly string DecoderId;
            public readonly string? DecodeKey;

            public RemotePayload(RemoteFileRequest request, string decoderId, string? decodeKey)
            {
                Request = request;
                DecoderId = decoderId;
                DecodeKey = decodeKey;
            }
        }
    }
}
