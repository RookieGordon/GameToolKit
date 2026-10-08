/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 内置本地文件加载器 (P5, §8.1/§8.2)。文件路径及解码参数 → 本地文件直接解码；
 *                不获得缓存文件所有权，不删除用户源文件；内存复用归 ResourceStore，
 *                不再有第二套 byte[] LRU。decoderId、目标表示进入 LocalKey。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common.Resource
{
    public sealed class LocalFileLoader : IResourceLoader
    {
        private readonly DecoderRegistry _decoders;

        public LocalFileLoader(DecoderRegistry decoders)
        {
            _decoders = decoders ?? throw new ArgumentNullException(nameof(decoders));
        }

        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(request.Address))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "reason", "empty-address" } }));
            }

            var decoderId = request.Parameters as string;
            var decoder = _decoders.Resolve(decoderId, request.RequestedType); // 未支持表示明确报错

            var path = _ToPath(request.Address);
            var localKey = string.Join("|",
                "local", path, decoder.Id, request.RequestedType.FullName ?? request.RequestedType.Name);
            return Task.FromResult(new ResolvedResource(localKey, request.RequestedType, path));
        }

        public async Task<LoadedAsset> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            var path = (string)resource.Payload!;
            try
            {
                // 解码器结果的释放操作即最终清理；源文件归项目所有，框架不删除
                return await _decoders
                    .Resolve(null, resource.RepresentationType)
                    .DecodeAsync(path, resource.RepresentationType, null, operationToken).ConfigureAwait(false);
            }
            catch (ResourceLoadException)
            {
                throw; // 解码器已按契约回退自身中间对象
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 自定义解码器的未分类异常：无法确认其中间对象是否清理
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetDecodeFailed, LoadStage.Decode, CleanupStatus.Unknown, ex,
                    new Dictionary<string, object> { { "path", path } }));
            }
        }

        private static string _ToPath(string address)
        {
            if (address.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return address.Substring("file://".Length);
            }
            return address;
        }
    }
}
