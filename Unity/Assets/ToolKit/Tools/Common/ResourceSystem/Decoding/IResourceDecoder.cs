/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 文件解码契约 (P5, §8.2)。DecoderRegistry 按显式 decoderId 或目标类型解析；
 *                找不到返回 asset.unsupported_representation，不尝试所有解码器碰运气。
 *                decoderId、目标表示与影响解码结果的参数进入加载器 LocalKey；
 *                失败契约与加载器一致：受控失败 Cleanup=Complete 并清理中间对象。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public interface IResourceDecoder
    {
        string Id { get; }

        /// <summary> 默认 true；只对完全读入结果的实现设 false (此时可提前释放源文件租约) </summary>
        bool RequiresSourceFile { get; }

        Task<LoadedAsset> DecodeAsync(string path, Type resultType, object? parameters, CancellationToken ct);
    }

    public sealed class DecoderRegistry
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, IResourceDecoder> _byId =
            new Dictionary<string, IResourceDecoder>(StringComparer.Ordinal);

        private readonly List<IResourceDecoder> _byRepresentation = new List<IResourceDecoder>();

        public static DecoderRegistry CreateDefault()
        {
            var registry = new DecoderRegistry();
            registry.Register(new BytesDecoder());
            registry.Register(new TextDecoder());
            return registry;
        }

        public void Register(IResourceDecoder decoder)
        {
            if (decoder == null) throw new ArgumentNullException(nameof(decoder));
            lock (_gate)
            {
                _byId[decoder.Id] = decoder;
                if (!_byRepresentation.Contains(decoder))
                {
                    _byRepresentation.Add(decoder);
                }
            }
        }

        /// <summary> 显式 decoderId 优先；否则按目标表示查找；找不到抛 asset.unsupported_representation </summary>
        public IResourceDecoder Resolve(string? decoderId, Type resultType)
        {
            lock (_gate)
            {
                if (decoderId != null)
                {
                    if (_byId.TryGetValue(decoderId, out var byId))
                    {
                        return byId;
                    }
                    throw new ResourceLoadException(_Unsupported(decoderId, resultType));
                }
                foreach (var decoder in _byRepresentation)
                {
                    if (decoder is IRepresentationDecoder typed && typed.CanDecode(resultType))
                    {
                        return decoder;
                    }
                    if (decoder is BytesDecoder && resultType == typeof(byte[]))
                    {
                        return decoder;
                    }
                    if (decoder is TextDecoder && resultType == typeof(string))
                    {
                        return decoder;
                    }
                }
            }
            throw new ResourceLoadException(_Unsupported("<auto>", resultType));
        }

        private static LoadError _Unsupported(string id, Type resultType)
        {
            return new LoadError(
                DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Decode, CleanupStatus.Complete, null,
                new Dictionary<string, object>
                {
                    { "decoderId", id },
                    { "resultType", resultType.Name },
                });
        }
    }

    /// <summary> 按目标表示自查的解码器 (引擎无关注册表用于自动解析) </summary>
    public interface IRepresentationDecoder
    {
        bool CanDecode(Type resultType);
    }

    /// <summary> byte[] 解码：完全读入；共享只读约定，需要修改的调用者自行复制 </summary>
    public sealed class BytesDecoder : IResourceDecoder, IRepresentationDecoder
    {
        public const string DecoderId = "bytes";

        public string Id => DecoderId;

        /// <summary> 结果完全读入内存，不依赖源文件 </summary>
        public bool RequiresSourceFile => false;

        public bool CanDecode(Type resultType)
        {
            return resultType == typeof(byte[]);
        }

        public async Task<LoadedAsset> DecodeAsync(string path, Type resultType, object? parameters, CancellationToken ct)
        {
            if (resultType != typeof(byte[]))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Decode, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "resultType", resultType.Name } }));
            }
            byte[] bytes;
            try
            {
                bytes = await ResourceFileIo.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 主动取消不是文件 I/O 故障 (R22)
            }
            catch (Exception ex)
            {
                throw new ResourceLoadException(_ReadError(path, ex));
            }
            return LoadedAsset.FromUnmanaged(bytes, bytes.LongLength);
        }

        internal static LoadError _ReadError(string path, Exception ex)
        {
            var code = ex is FileNotFoundException || ex is DirectoryNotFoundException
                ? DiagnosticCodes.FileNotFound
                : FileSystemErrorClassifier.IsDiskFull(ex)
                    ? DiagnosticCodes.CacheDiskFull
                    : FileSystemErrorClassifier.IsAccessDenied(ex)
                        ? DiagnosticCodes.FileAccessDenied
                        : DiagnosticCodes.FileIoFailed;
            return new LoadError(code, LoadStage.ReadFile, CleanupStatus.Complete, ex,
                new Dictionary<string, object> { { "path", path } });
        }
    }

    /// <summary> 文本解码：UTF-8 读入字符串 </summary>
    public sealed class TextDecoder : IResourceDecoder, IRepresentationDecoder
    {
        public const string DecoderId = "text";

        public string Id => DecoderId;
        public bool RequiresSourceFile => false;

        public bool CanDecode(Type resultType)
        {
            return resultType == typeof(string);
        }

        public async Task<LoadedAsset> DecodeAsync(string path, Type resultType, object? parameters, CancellationToken ct)
        {
            if (resultType != typeof(string))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Decode, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "resultType", resultType.Name } }));
            }
            try
            {
                using var reader = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var content = await reader.ReadToEndAsync().ConfigureAwait(false);
                return LoadedAsset.FromUnmanaged(content, content.Length * sizeof(char));
            }
            catch (Exception ex)
            {
                throw new ResourceLoadException(BytesDecoder._ReadError(path, ex));
            }
        }
    }
}
