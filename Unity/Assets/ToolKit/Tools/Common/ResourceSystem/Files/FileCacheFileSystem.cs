/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 文件系统抽象 (P4)。仅为可注入错误的目的建立 (§14.1 FakeFileSystem)，
 *                覆盖 FileCache 实际需要的最小操作集；物理实现包装 System.IO。
 *                "文件已不存在"的删除视为成功。
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common.Resource
{
    public interface IFileCacheFileSystem
    {
        bool FileExists(string path);
        void CreateDirectory(string path);
        Stream OpenRead(string path);
        Stream CreateWrite(string path);
        long GetFileLength(string path);
        void DeleteFile(string path);
        void MoveFile(string source, string destination, bool overwrite);
        string[] GetFiles(string directory);
        string[] GetDirectories(string directory);
        DateTime GetLastWriteTimeUtc(string path);
    }

    public sealed class PhysicalFileCacheFileSystem : IFileCacheFileSystem
    {
        public static readonly PhysicalFileCacheFileSystem Instance = new PhysicalFileCacheFileSystem();

        public bool FileExists(string path)
        {
            return File.Exists(path);
        }

        public void CreateDirectory(string path)
        {
            Directory.CreateDirectory(path);
        }

        public Stream OpenRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        }

        public Stream CreateWrite(string path)
        {
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        }

        public long GetFileLength(string path)
        {
            return new FileInfo(path).Length;
        }

        public void DeleteFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public void MoveFile(string source, string destination, bool overwrite)
        {
            // Unity 运行时缺少 File.Move(src, dst, bool) 重载：
            // 覆盖语义优先用 File.Replace (原子替换)，平台不支持时退化为 删除旧目标 + 移动
            if (overwrite && File.Exists(destination))
            {
                try
                {
                    File.Replace(source, destination, null);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(destination); // 非原子回退：崩溃由启动恢复按孤立文件处理
                }
            }
            File.Move(source, destination);
        }

        public string[] GetFiles(string directory)
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory) : Array.Empty<string>();
        }

        public string[] GetDirectories(string directory)
        {
            return Directory.Exists(directory) ? Directory.GetDirectories(directory) : Array.Empty<string>();
        }

        public DateTime GetLastWriteTimeUtc(string path)
        {
            return File.GetLastWriteTimeUtc(path);
        }
    }

    /// <summary>
    /// 磁盘满/权限错误识别 (公开供 Unity 侧解码器复用)：
    /// ERROR_DISK_FULL (0x80070070) 与常见消息 (F10 不误报 network)
    /// </summary>
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

    /// <summary>
    /// 文件读取助手：Unity 运行时缺少部分 netstandard2.1 的异步 File API
    /// (如 File.ReadAllBytesAsync)，统一使用 FileStream 手动异步读取。
    /// </summary>
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
}
