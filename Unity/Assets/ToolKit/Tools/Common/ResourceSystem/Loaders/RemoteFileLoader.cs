/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 内置远端文件加载器 (P5, §8.1/§8.2)。FileCache 获取本地文件 (FileLease) 后解码；
 *                先清理解码结果、再归还仍需持有的文件租约。FileIdentity 只标识原始文件，
 *                不带不影响字节内容的解码区别，多种内存表示复用同一下载文件；
 *                decoderId/目标表示进入 LocalKey，文件 TTL 不承诺业务对象热更新 (§10.2)。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common.Resource
{
    public sealed class RemoteFileLoader : IResourceLoader
    {
        private readonly FileCache _fileCache;
        private readonly DecoderRegistry _decoders;
        private readonly Func<string, FileRequest>? _requestBuilder;

        /// <param name="requestBuilder">业务可注入的 URL → FileRequest 映射 (版本/租户/校验)；默认按 URL 生成不可变身份</param>
        public RemoteFileLoader(FileCache fileCache, DecoderRegistry decoders,
            Func<string, FileRequest>? requestBuilder = null)
        {
            _fileCache = fileCache ?? throw new ArgumentNullException(nameof(fileCache));
            _decoders = decoders ?? throw new ArgumentNullException(nameof(decoders));
            _requestBuilder = requestBuilder;
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
            var decoderId = request.Parameters as string;
            var decoder = _decoders.Resolve(decoderId, request.RequestedType); // 未注册支持目标类型的解码器明确报错

            // 文件身份 + 解码表示构成资源身份；同一下载文件可支撑多种内存表示
            var localKey = string.Join("|",
                "remote", fileRequest.Identity.ToString(), decoder.Id,
                request.RequestedType.FullName ?? request.RequestedType.Name);
            return Task.FromResult(new ResolvedResource(localKey, request.RequestedType,
                new RemotePayload(fileRequest, decoder.Id)));
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
                decoded = await decoder.DecodeAsync(file.Path, resource.RepresentationType, null, operationToken)
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
                decoded?.ReleaseAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                file?.Dispose();
                throw;
            }
            catch (ResourceLoadException rle)
            {
                var error = rle.Error;
                try
                {
                    if (decoded != null)
                    {
                        await decoded.ReleaseAsync().ConfigureAwait(false); // 尝试一次回退
                    }
                }
                catch (Exception rollbackEx)
                {
                    error = error.WithRelated(new LoadError(
                        DiagnosticCodes.LifecycleReleaseFailed, LoadStage.Decode,
                        CleanupStatus.Incomplete, rollbackEx));
                }
                file?.Dispose();
                throw new ResourceLoadException(error); // 保留原始错误与 DiagnosticId 关联链
            }
            catch (Exception ex)
            {
                // 未分类异常：无法确认解码器中间对象是否清理，不猜测
                file?.Dispose();
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetDecodeFailed, LoadStage.Decode, CleanupStatus.Unknown, ex,
                    new Dictionary<string, object> { { "url", _Sanitize(payload.Request.Source) } }));
            }
        }

        /// <summary> 默认身份：查询串 (临时签名) 不进入身份；URL 相同但版本身份不同由业务 builder 切换 Revision </summary>
        private static FileRequest _DefaultRequest(Uri uri)
        {
            var contentPath = uri.Scheme + "://" + uri.Host + uri.AbsolutePath;
            using var sha = SHA256.Create();
            var revision = _ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(contentPath)));
            var identity = new FileIdentity("remote", uri.Host + uri.AbsolutePath, revision, "");
            return new FileRequest(identity, uri, null, FileValidity.Immutable);
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

            public RemotePayload(FileRequest request, string decoderId)
            {
                Request = request;
                DecoderId = decoderId;
            }
        }
    }
}
