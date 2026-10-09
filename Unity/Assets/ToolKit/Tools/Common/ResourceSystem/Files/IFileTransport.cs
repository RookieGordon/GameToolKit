/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 传输契约 (P4, §10.1)。IFileTransport 只执行一次流式远端读取：
 *                不缓存文件、不决定保存目录、不操作缓存索引。重试、并发合并、
 *                存储容量与本地命中由 FileCache 负责。TransportResponse 只前进读取，
 *                不隐藏整文件内存缓冲或磁盘缓存。
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public interface IFileTransport
    {
        Task<TransportResponse> OpenReadAsync(FileRequest request, CancellationToken cancellationToken);
    }

    public sealed class TransportResponse : IAsyncDisposable
    {
        public readonly Stream Body;
        public readonly long? ContentLength;
        public readonly int? StatusCode;
        public readonly string? ETag;

        private readonly Action? _dispose;
        private int _disposed;

        public TransportResponse(Stream body, long? contentLength, int? statusCode, string? etag, Action? dispose)
        {
            Body = body;
            ContentLength = contentLength;
            StatusCode = statusCode;
            ETag = etag;
            _dispose = dispose;
        }

        /// <summary> 尽力停止底层 I/O；不代替完成后的清理 </summary>
        public void Abort()
        {
            try
            {
                Body.Close();
            }
            catch (Exception)
            {
                // Abort 尽力而为
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            try
            {
                await Body.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _dispose?.Invoke();
            }
        }
    }
}
