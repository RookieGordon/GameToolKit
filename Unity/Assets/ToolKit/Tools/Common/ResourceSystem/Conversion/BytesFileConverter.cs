using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 文件完整读入 byte[]；共享只读约定，需要修改的调用者自行复制。 </summary>
    public sealed class BytesFileConverter : IFileResourceConverter
    {
        public async Task<LoadedAsset> ConvertAsync(string path, Type resultType, string? conversionKey, CancellationToken ct)
        {
            if (resultType != typeof(byte[]))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "resultType", resultType.Name } }));
            }
            byte[] bytes;
            try
            {
                ct.ThrowIfCancellationRequested();
                bytes = await ResourceFileIo.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw new ResourceLoadException(FileSystemErrorClassifier.ToLoadError(path, ex, LoadStage.ReadFile));
            }
            return LoadedAsset.FromUnmanaged(bytes);
        }

    }
}

