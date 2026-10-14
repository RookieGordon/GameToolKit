/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 内置本地文件加载器 (P5, §8.1/§8.2)。文件路径及转换参数 → 目标资源对象；
 *                不获得缓存文件所有权，不删除用户源文件；内存复用由 LoadManager 协调，
 *                不再有第二套 byte[] LRU。R18：目标类型与转换参数进入 LocalKey 并传给转换器，
 *                不支持的参数明确拒绝。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public sealed class LocalFileLoader : IResourceLoader
    {
        private readonly FileResourceConverters _converters;

        public LocalFileLoader(FileResourceConverters converters)
        {
            _converters = converters ?? throw new ArgumentNullException(nameof(converters));
        }

        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(request.Address))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "reason", "empty-address" } }));
            }

            var conversionKey = FileLoadParameters.GetConversionKey(request.Parameters);
            var converter = _converters.Get(request.RequestedType);

            var path = _ToPath(request.Address);
            var localKey = string.Join("|",
                "local", path,
                request.RequestedType.AssemblyQualifiedName ?? request.RequestedType.FullName ?? request.RequestedType.Name,
                conversionKey ?? "");
            return Task.FromResult(new ResolvedResource(
                localKey, request.RequestedType, new LocalPayload(path, converter, conversionKey)));
        }

        public async Task<LoadedAsset> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            var payload = (LocalPayload)resource.Payload!;
            try
            {
                // 直接使用本次解析选定的转换器；结果自带释放操作，源文件仍归项目所有。
                return await payload.Converter
                    .ConvertAsync(payload.Path, resource.RepresentationType, payload.ConversionKey, operationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not ResourceLoadException && ex is not OperationCanceledException)
            {
                // 自定义转换器的未分类异常：无法确认其中间对象是否清理
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetConversionFailed, LoadStage.Convert, CleanupStatus.Unknown, ex,
                    new Dictionary<string, object> { { "path", payload.Path } }));
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

        private sealed class LocalPayload
        {
            public readonly string Path;
            public readonly IFileResourceConverter Converter;
            public readonly string? ConversionKey;

            public LocalPayload(string path, IFileResourceConverter converter, string? conversionKey)
            {
                Path = path;
                Converter = converter;
                ConversionKey = conversionKey;
            }
        }
    }
}
