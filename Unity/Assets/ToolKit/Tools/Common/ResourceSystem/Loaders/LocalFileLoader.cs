/*
 * description  : 本地文件来源：按路径读出 byte[] 产物；业务要别的类型时由按目标类型注册的转换器转换。
 *                Address 是文件路径，相对路径按 BaseDirectory 解析。读取在线程池异步完成，观察本轮取消。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public sealed class LocalFileLoader : ILoader
    {
        private readonly string _baseDirectory;

        /// <param name="baseDirectory">相对路径的基准目录；null 表示按当前工作目录</param>
        public LocalFileLoader(string baseDirectory = null)
        {
            _baseDirectory = baseDirectory;
        }

        public string Resolve(string address)
        {
            return Path.IsPathRooted(address) || string.IsNullOrEmpty(_baseDirectory)
                ? Path.GetFullPath(address)
                : Path.GetFullPath(Path.Combine(_baseDirectory, address));
        }

        /// <summary> 纯文件 IO：不需要引擎，忽略 context </summary>
        public async Task<IResource> LoadAsync(ResourceKey key, IExecutionContext context, IProgress<OperationProgress> progress, CancellationToken cancellation)
        {
            var path = Resolve(key.Address);
            if (!File.Exists(path)) throw ResourceException.Of(DiagnosticCodes.FileNotFound, LoadStage.ReadFile, key);
            return new Product(await ReadAllBytesAsync(path, key, progress, cancellation).ConfigureAwait(false));
        }

        /// <summary> 读整个文件；IO 错误映射为资源诊断码。远程来源读取已发布的缓存文件时复用。 </summary>
        internal static async Task<byte[]> ReadAllBytesAsync(string path, ResourceKey key, IProgress<OperationProgress> progress, CancellationToken cancellation)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[stream.Length];
                    var offset = 0;
                    while (offset < buffer.Length)
                    {
                        var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellation).ConfigureAwait(false);
                        if (read == 0) throw new EndOfStreamException("文件在读取过程中被截短: " + path);
                        offset += read;
                        progress?.Report(new OperationProgress(buffer.Length == 0 ? 1 : (double)offset / buffer.Length, "读取文件"));
                    }
                    return buffer;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException error) { throw ResourceException.Of(DiagnosticCodes.FileAccessDenied, LoadStage.ReadFile, key, error); }
            catch (FileNotFoundException error) { throw ResourceException.Of(DiagnosticCodes.FileNotFound, LoadStage.ReadFile, key, error); }
            catch (DirectoryNotFoundException error) { throw ResourceException.Of(DiagnosticCodes.FileNotFound, LoadStage.ReadFile, key, error); }
            catch (IOException error) { throw ResourceException.Of(DiagnosticCodes.FileIoFailed, LoadStage.ReadFile, key, error); }
        }
    }

    /// <summary> 内置转换：byte[] → string (默认 UTF-8)。注册到目标类型 string。 </summary>
    public sealed class TextConverter : IProductConverter
    {
        private readonly Encoding _encoding;

        public TextConverter(Encoding encoding = null)
        {
            _encoding = encoding ?? new UTF8Encoding(false, true);
        }

        public Task<IResource> ConvertAsync(IResource source, ResourceKey key, CancellationToken cancellation)
        {
            if (!(source.Value is byte[] bytes))
                throw ResourceException.Of(DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, key);
            return Task.FromResult<IResource>(new Product(_encoding.GetString(bytes)));
        }
    }
}
