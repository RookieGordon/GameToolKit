using System;
using System.Collections.Generic;
using System.IO;
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
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
}

