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
    public sealed class LocalFileLoader : IResourceLoader, IConvertingLoader
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

        public Task<LoadedContent> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            var payload = (LocalPayload)resource.Payload!;
            // 取内容归加载器：读文件与文件系统异常映射都在这一层；源文件仍归项目所有。
            return Task.FromResult(new LoadedContent(
                FileConversion.ReadBytes(payload.Path, operationToken)));
        }

        /// <summary> 转换本来源的内容；由资源编排在取到内容之后调用 </summary>
        Task<LoadedAsset> IConvertingLoader.ConvertAsync(
            LoadedContent content, ResolvedResource resource, CancellationToken operationToken)
        {
            var payload = (LocalPayload)resource.Payload!;
            return FileConversion.ConvertAsync(
                content, payload.Converter, resource.RepresentationType, payload.ConversionKey, operationToken,
                payload.Path);
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
