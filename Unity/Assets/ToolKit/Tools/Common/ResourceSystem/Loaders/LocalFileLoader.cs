/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : 本地文件加载器 (引擎无关)。把本地路径的文件读为 byte[] 并包装成 AssetHandle。
 *                优化:
 *                  1. 内存内容缓存 (LRU) —— 句柄引用归零后内容仍在缓存中保留一段, 再次请求直接复用,
 *                     省去重复读盘 (相当于弱缓存 / 延迟卸载)。超过容量按最近最少使用淘汰。
 *                  2. 字节缓冲区池 —— 读盘的传输缓冲走 BytesPool, 降低大文件读取的堆分配与 GC。
 *                注意: 缓存的 byte[] 在多个调用方间共享, 约定为只读, 不可原地修改。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public sealed class LocalFileLoader : ILoader
    {
        private const int ReadChunkSize = 81920;

        private readonly int _cacheCapacity;
        private readonly long _maxCacheableBytes;

        // —— LRU 内存内容缓存 ——
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, byte[]>>> _cacheMap;
        private readonly LinkedList<KeyValuePair<string, byte[]>> _cacheList;
        private readonly object _cacheGate = new object();

        public ELoadType LoadType => ELoadType.LocalFile;

        public int MaxConcurrentLoads { get; }

        /// <param name="cacheCapacity">内存内容缓存条目上限, &lt;=0 关闭缓存</param>
        /// <param name="maxCacheableBytes">单文件可缓存的最大字节数, 超过则不进缓存 (避免大文件占内存)</param>
        /// <param name="maxConcurrentLoads">最大并发读取数, &lt;=0 表示不限制</param>
        public LocalFileLoader(int cacheCapacity = 32, long maxCacheableBytes = 512 * 1024, int maxConcurrentLoads = 0)
        {
            _cacheCapacity = cacheCapacity;
            _maxCacheableBytes = maxCacheableBytes;
            MaxConcurrentLoads = maxConcurrentLoads;
            if (cacheCapacity > 0)
            {
                _cacheMap = new Dictionary<string, LinkedListNode<KeyValuePair<string, byte[]>>>(cacheCapacity);
                _cacheList = new LinkedList<KeyValuePair<string, byte[]>>();
            }
        }

        public bool CanLoad(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                return false;
            }
            if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return Path.IsPathRooted(address) || address.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<IAssetHandle> LoadAsync(string address, CancellationToken cancellationToken = default)
        {
            var handle = new AssetHandle(address);

            // 1. 命中内存缓存 -> 直接复用
            if (_TryGetCached(address, out var cachedBytes))
            {
                handle.SetSucceed(cachedBytes, null);
                return handle;
            }

            var path = address.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? address.Substring("file://".Length)
                : address;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(path))
                {
                    handle.SetFailed(ELoadError.NotFound, $"本地文件不存在: {path}");
                    return handle;
                }

                var bytes = await _ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

                _TryAddCache(address, bytes);
                handle.SetSucceed(bytes, null); // byte[] 无需特殊卸载
            }
            catch (OperationCanceledException)
            {
                handle.SetCancelled();
            }
            catch (Exception e)
            {
                handle.SetFailed(ELoadError.IOError, $"读取本地文件失败: {path}", e);
            }

            return handle;
        }

        /// <summary> 读盘: 传输缓冲走 BytesPool, 结果数组按文件长度一次分配 </summary>
        private static async Task<byte[]> _ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ReadChunkSize, useAsync: true))
            {
                var result = new byte[fs.Length];
                if (result.Length == 0)
                {
                    return result;
                }

                var buffer = BytesPool.Rent(ReadChunkSize);
                try
                {
                    int written = 0;
                    int read;
                    while ((read = await fs.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        Buffer.BlockCopy(buffer, 0, result, written, read);
                        written += read;
                    }
                }
                finally
                {
                    BytesPool.Return(buffer);
                }
                return result;
            }
        }

        #region LRU 缓存

        private bool _TryGetCached(string address, out byte[] bytes)
        {
            bytes = null;
            if (_cacheMap == null)
            {
                return false;
            }
            lock (_cacheGate)
            {
                if (_cacheMap.TryGetValue(address, out var node))
                {
                    _cacheList.Remove(node);
                    _cacheList.AddFirst(node); // 提升为最近使用
                    bytes = node.Value.Value;
                    return true;
                }
            }
            return false;
        }

        private void _TryAddCache(string address, byte[] bytes)
        {
            if (_cacheMap == null || bytes.LongLength > _maxCacheableBytes)
            {
                return;
            }
            lock (_cacheGate)
            {
                if (_cacheMap.ContainsKey(address))
                {
                    return;
                }
                var node = _cacheList.AddFirst(new KeyValuePair<string, byte[]>(address, bytes));
                _cacheMap[address] = node;

                while (_cacheMap.Count > _cacheCapacity)
                {
                    var last = _cacheList.Last;
                    _cacheList.RemoveLast();
                    _cacheMap.Remove(last.Value.Key);
                }
            }
        }

        /// <summary> 清空内存内容缓存 </summary>
        public void ClearCache()
        {
            if (_cacheMap == null)
            {
                return;
            }
            lock (_cacheGate)
            {
                _cacheMap.Clear();
                _cacheList.Clear();
            }
        }

        #endregion
    }
}
