/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 文件租约 (P4, §10.1)。对实际本地文件的一次占用，pin 具体代次；
 *                Dispose 幂等、对象不池化、始终归还同一 FileEntry。
 */

using System;
using System.Threading;

namespace ToolKit.Tools.Common.Resource
{
    public sealed class FileLease : IDisposable
    {
        private readonly FileCache _cache;
        private readonly FileEntry _entry;
        private int _disposed;

        internal FileLease(FileCache cache, FileEntry entry)
        {
            _cache = cache;
            _entry = entry;
        }

        /// <summary> 本地文件完整路径。Dispose 后访问抛 ObjectDisposedException </summary>
        public string Path
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(FileLease),
                        $"文件租约已释放 (key={_entry.Key}, generation={_entry.Generation})");
                }
                return _entry.DataPath;
            }
        }

        public FileIdentity Identity => _entry.Identity;

        internal FileEntry Entry => _entry;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _cache.ReleaseLease(_entry);
        }
    }
}
