using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 文件以 UTF-8 读入字符串，存在字节顺序标记时采用对应编码。 </summary>
    public sealed class TextFileConverter : IFileResourceConverter
    {
        public async Task<LoadedAsset> ConvertAsync(string path, Type resultType, string? conversionKey, CancellationToken ct)
        {
            if (resultType != typeof(string))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "resultType", resultType.Name } }));
            }
            try
            {
                ct.ThrowIfCancellationRequested();
                using var reader = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var content = await reader.ReadToEndAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return LoadedAsset.FromUnmanaged(content);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ResourceLoadException(BytesFileConverter._ReadError(path, ex));
            }
        }
    }
}
