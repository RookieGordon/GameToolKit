/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 目标类型 → 文件转换器的精确注册与查询 (需求第 7 条)，以及框架内置实现。
 *                注册表按目标类型精确匹配，不做基类或接口回退推断；未注册的类型在解析阶段就明确拒绝，
 *                不等到读文件之后。内置转换只在文件系统上读取，不持有缓存文件所有权、不删除缓存文件。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 文件转换器注册表；公开类型名保留 FileResourceConverters </summary>
    public sealed class FileResourceConverters
    {
        private readonly Dictionary<Type, IFileResourceConverter> _converters =
            new Dictionary<Type, IFileResourceConverter>();

        /// <summary> 注册或替换某个目标类型的转换器 </summary>
        public void Register(Type resultType, IFileResourceConverter converter)
        {
            if (resultType == null) throw new ArgumentNullException(nameof(resultType));
            if (converter == null) throw new ArgumentNullException(nameof(converter));
            _converters[resultType] = converter;
        }

        /// <summary> 按目标类型精确查询；未注册时抛出可诊断错误 </summary>
        public IFileResourceConverter Get(Type resultType)
        {
            if (resultType == null) throw new ArgumentNullException(nameof(resultType));
            if (_converters.TryGetValue(resultType, out var converter)) return converter;
            throw new ResourceLoadException(new LoadError(
                DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Resolve, CleanupStatus.Complete, null,
                new Dictionary<string, object> { { "resultType", resultType.Name } }));
        }

        /// <summary> 内置实现：byte[] 与 string </summary>
        public static FileResourceConverters CreateDefault()
        {
            var converters = new FileResourceConverters();
            converters.Register(typeof(byte[]), new BytesFileConverter());
            converters.Register(typeof(string), new TextFileConverter());
            return converters;
        }
    }

    /// <summary> 共同文件 I/O：把文件系统异常映射为带阶段与路径的诊断错误 </summary>
    internal static class ResourceFileIo
    {
        internal static byte[] ReadAllBytes(string path, LoadStage stage)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new ResourceLoadException(new LoadError(
                    _DiagnosticOf(error), stage, CleanupStatus.Complete, error,
                    new Dictionary<string, object> { { "path", path } }));
            }
        }

        private static string _DiagnosticOf(Exception error)
        {
            if (error is FileNotFoundException || error is DirectoryNotFoundException)
                return DiagnosticCodes.FileNotFound;
            if (error is UnauthorizedAccessException)
                return DiagnosticCodes.FileAccessDenied;
            return DiagnosticCodes.FileIoFailed;
        }
    }

    /// <summary> byte[]：直接读取整个文件 </summary>
    public sealed class BytesFileConverter : IFileResourceConverter
    {
        public Task<LoadedAsset> ConvertAsync(
            string path, Type resultType, string? conversionKey, CancellationToken cancellationToken)
        {
            if (resultType != typeof(byte[]))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, CleanupStatus.Complete,
                    null, new Dictionary<string, object> { { "resultType", resultType.Name } }));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = ResourceFileIo.ReadAllBytes(path, LoadStage.ReadFile);
            return Task.FromResult(LoadedAsset.FromUnmanaged(bytes));
        }
    }

    /// <summary> string：按 UTF-8 读取文本；换行保持文件原样，不做平台归一化 </summary>
    public sealed class TextFileConverter : IFileResourceConverter
    {
        public Task<LoadedAsset> ConvertAsync(
            string path, Type resultType, string? conversionKey, CancellationToken cancellationToken)
        {
            if (resultType != typeof(string))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, CleanupStatus.Complete,
                    null, new Dictionary<string, object> { { "resultType", resultType.Name } }));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = ResourceFileIo.ReadAllBytes(path, LoadStage.ReadFile);
            return Task.FromResult(LoadedAsset.FromUnmanaged(Encoding.UTF8.GetString(bytes)));
        }
    }
}
