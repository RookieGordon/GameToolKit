/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 文件读取与错误分类助手 (自 FileCacheFileSystem 迁出，删减版保留部分)。
 *                Unity 运行时缺少部分 netstandard2.1 的异步 File API，统一使用 FileStream 手动异步读取。
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 文件读取助手：绕开 Unity 缺失的 File.ReadAllBytesAsync </summary>
    public static class ResourceFileIo
    {
        public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, useAsync: true);
            using var memory = new MemoryStream((int)stream.Length);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                await memory.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
            }
            return memory.ToArray();
        }
    }

    /// <summary> 磁盘满/权限错误识别：ERROR_DISK_FULL (0x80070070) 与常见消息 </summary>
    public static class FileSystemErrorClassifier
    {
        private const int ErrorDiskFullHResult = unchecked((int)0x80070070);

        public static bool IsDiskFull(Exception ex)
        {
            if (ex.HResult == ErrorDiskFullHResult)
            {
                return true;
            }
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e.HResult == ErrorDiskFullHResult)
                {
                    return true;
                }
                var message = e.Message;
                if (!string.IsNullOrEmpty(message)
                    && (message.IndexOf("disk full", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("not enough space on the disk", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("磁盘空间不足", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsAccessDenied(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is UnauthorizedAccessException)
                {
                    return true;
                }
                if (e.Message != null && e.Message.IndexOf("access", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
