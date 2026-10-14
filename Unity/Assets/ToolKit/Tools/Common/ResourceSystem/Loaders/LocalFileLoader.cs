/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 内置本地文件加载器。文件路径 + 目标类型 + 转换参数 → 目标资源对象；
 *                不获得缓存文件所有权，不删除用户源文件，也没有第二套内存缓存。
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
            // 路径 + 目标类型 + 转换参数构成资源身份：同一文件可支撑多种内存表示。
            var localKey = string.Join("|", "local", path, _TypeKey(request.RequestedType), conversionKey ?? "");
            return Task.FromResult(new ResolvedResource(
                localKey, request.RequestedType, new LocalPayload(path, converter, conversionKey)));
        }

        public async Task<LoadedAsset> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            var payload = (LocalPayload)resource.Payload!;
            try
            {
                // 使用本次解析选定的转换器；源文件仍归项目所有。
                return await payload.Converter
                    .ConvertAsync(payload.Path, resource.RepresentationType, payload.ConversionKey, operationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not ResourceLoadException and not OperationCanceledException)
            {
                // 自定义转换器的未分类异常：无法确认其中间对象是否清理，按 Unknown 报出。
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetConversionFailed, LoadStage.Convert, CleanupStatus.Unknown, error,
                    new Dictionary<string, object> { { "path", payload.Path } }));
            }
        }

        internal static string _TypeKey(Type type) =>
            type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

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
            internal LocalPayload(string path, IFileResourceConverter converter, string? conversionKey)
            {
                Path = path;
                Converter = converter;
                ConversionKey = conversionKey;
            }

            internal string Path { get; }

            internal IFileResourceConverter Converter { get; }

            internal string? ConversionKey { get; }
        }
    }
}
