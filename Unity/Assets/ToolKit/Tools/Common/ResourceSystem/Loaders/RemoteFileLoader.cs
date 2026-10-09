/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 内置远端文件加载器 (P5, §8.1/§8.2)。FileCache 获取本地文件 (FileLease) 后解码；
 *                先清理解码结果、再归还仍需持有的文件租约。FileIdentity 只标识原始文件，
 *                decoderId/目标表示/解码参数进入 LocalKey，多种内存表示复用同一下载文件。
 *                R01：默认身份保留影响资源内容的完整 URL (端口/查询)，默认请求带 TTL，
 *                不得把未版本化可变地址当永久有效；只有业务 builder 给出稳定身份时才合并签名 URL。
 *                R06：解码回退未确认完成时保留文件租约到加载器隔离记录，不提前放开 pin。
 */

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public sealed class RemoteFileLoader : IResourceLoader
    {
        /// <summary> 无明确版本动态 URL 的默认有效期；内容更新由业务切换版本身份或缩短 TTL </summary>
        public static readonly TimeSpan DefaultDynamicUrlTtl = TimeSpan.FromHours(24);

        private readonly FileCache _fileCache;
        private readonly DecoderRegistry _decoders;
        private readonly Func<string, FileRequest>? _requestBuilder;
        private readonly FileValidity _defaultValidity;
        private readonly IResourceDiagnostics _diagnostics;

        /// <summary> 回退未确认完成而保留的文件租约：仍可能被中间对象读取，pin 不放开 (R06) </summary>
        private readonly List<FileLease> _quarantinedLeases = new List<FileLease>();

        /// <param name="requestBuilder">业务注入的 URL → FileRequest 映射：提供稳定 ArtifactId/Revision 时签名 URL 可合并；有效性由请求自带</param>
        /// <param name="defaultValidity">默认请求 (无 builder) 的有效性；默认 24h TTL</param>
        public RemoteFileLoader(FileCache fileCache, DecoderRegistry decoders,
            Func<string, FileRequest>? requestBuilder = null,
            FileValidity? defaultValidity = null,
            IResourceDiagnostics? diagnostics = null)
        {
            _fileCache = fileCache ?? throw new ArgumentNullException(nameof(fileCache));
            _decoders = decoders ?? throw new ArgumentNullException(nameof(decoders));
            _requestBuilder = requestBuilder;
            _defaultValidity = defaultValidity ?? FileValidity.ExpiresAfter(DefaultDynamicUrlTtl);
            _diagnostics = new SafeResourceDiagnostics(diagnostics ?? NullResourceDiagnostics.Instance);
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

            var fileRequest = _requestBuilder != null
                ? _requestBuilder(request.Address)
                : _DefaultRequest(uri);
            var parameters = FileLoadParameters.From(request.Parameters);
            var decoder = _decoders.Resolve(parameters.DecoderId, request.RequestedType);

            // 文件身份 + 解码表示 + 解码参数构成资源身份；同一下载文件可支撑多种内存表示
            var localKey = string.Join("|",
                "remote", fileRequest.Identity.ToString(), decoder.Id,
                request.RequestedType.FullName ?? request.RequestedType.Name, parameters.DecodeKey ?? "");
            return Task.FromResult(new ResolvedResource(localKey, request.RequestedType,
                new RemotePayload(fileRequest, decoder.Id, parameters.DecodeKey)));
        }

        public async Task<LoadedAsset> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            var payload = (RemotePayload)resource.Payload!;
            var decoder = _decoders.Resolve(payload.DecoderId, resource.RepresentationType);

            FileLease? file = null;
            LoadedAsset? decoded = null;
            try
            {
                file = await _fileCache.AcquireAsync(payload.Request, operationToken).ConfigureAwait(false);
                decoded = await decoder.DecodeAsync(file.Path, resource.RepresentationType, payload.DecodeKey, operationToken)
                    .ConfigureAwait(false);

                var sourceFile = decoder.RequiresSourceFile ? file : null;
                if (!decoder.RequiresSourceFile)
                {
                    file.Dispose(); // 完全读入结果的实现可立即释放源文件占用
                    file = null;
                }

                // 从此开始已取得成功结果；取消也不能丢掉它 (§8.2)
                var fileLease = sourceFile;
                return new LoadedAsset(
                    decoded.Value,
                    decoded.EstimatedBytes,
                    decoded.IsAlive,
                    async () =>
                    {
                        await decoded.ReleaseAsync().ConfigureAwait(false);
                        fileLease?.Dispose(); // 解码结果释放成功后才放开仍可能被读取的文件
                    });
            }
            catch (OperationCanceledException)
            {
                // 解码取消：解码器契约保证回退自身中间对象；文件占用归还
                file?.Dispose();
                throw;
            }
            catch (ResourceLoadException rle)
            {
                var error = rle.Error;
                var confirmed = false;
                if (decoded != null)
                {
                    try
                    {
                        await decoded.ReleaseAsync().ConfigureAwait(false); // 尝试一次回退
                        confirmed = true;
                    }
                    catch (Exception rollbackEx)
                    {
                        error = error.WithRelated(new LoadError(
                            DiagnosticCodes.LifecycleReleaseFailed, LoadStage.Decode,
                            CleanupStatus.Incomplete, rollbackEx));
                    }
                }
                else
                {
                    confirmed = error.Cleanup == CleanupStatus.Complete;
                }
                if (file != null)
                {
                    if (confirmed)
                    {
                        file.Dispose();
                    }
                    else
                    {
                        // 回退未确认完成：保留文件租约到隔离记录，容量淘汰不能删除仍被中间对象使用的文件 (R06)
                        _quarantinedLeases.Add(file);
                        _diagnostics.Report(new LoadError(
                            DiagnosticCodes.LifecycleReleaseFailed, LoadStage.Decode,
                            CleanupStatus.Incomplete, null,
                            new Dictionary<string, object>
                            {
                                { "url", _Sanitize(payload.Request.Source) },
                                { "fact", "file-lease-quarantined" },
                            }));
                    }
                }
                throw new ResourceLoadException(error); // 保留原始错误与 DiagnosticId 关联链
            }
            catch (Exception ex)
            {
                // 未分类异常：无法确认解码器中间对象是否清理；文件租约同样隔离 (R06)
                if (file != null)
                {
                    _quarantinedLeases.Add(file);
                }
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetDecodeFailed, LoadStage.Decode, CleanupStatus.Unknown, ex,
                    new Dictionary<string, object> { { "url", _Sanitize(payload.Request.Source) } }));
            }
        }

        /// <summary>
        /// 默认身份 (R01)：保留影响资源内容的完整 URL —— 端口与查询参数都参与身份
        /// (查询不同视为不同内容；身份可哈希，日志单独脱敏)。revision 为完整 URL 的摘要，
        /// 有效性为默认 TTL：服务器替换同 URL 内容后到期重新下载，不默认永久信任。
        /// </summary>
        private FileRequest _DefaultRequest(Uri uri)
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
            return new FileRequest(identity, uri, null, _defaultValidity);
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
            public readonly FileRequest Request;
            public readonly string DecoderId;
            public readonly string? DecodeKey;

            public RemotePayload(FileRequest request, string decoderId, string? decodeKey)
            {
                Request = request;
                DecoderId = decoderId;
                DecodeKey = decodeKey;
            }
        }
    }
}
