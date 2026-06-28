/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : 按 key 的异步锁。同一 key 的临界区串行化, 不同 key 互不阻塞, 全程不阻塞线程。
 *                典型用途: 保证"同一个资源地址在并发请求下只真正加载一次"。
 *
 * 用法:
 *     using (await _lock.LockAsync(address, ct))
 *     {
 *         // 同一 address 的代码块在此串行执行
 *     }
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public sealed class KeyedAsyncLock
    {
        /// <summary>
        /// 释放句柄。dispose 时归还信号量, 引用计数归零则回收, 避免字典无限膨胀。
        /// </summary>
        public readonly struct Releaser : IDisposable
        {
            private readonly KeyedAsyncLock _owner;
            private readonly string _key;

            internal Releaser(KeyedAsyncLock owner, string key)
            {
                _owner = owner;
                _key = key;
            }

            public void Dispose()
            {
                _owner?._Release(_key);
            }
        }

        private sealed class Entry
        {
            public readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
            /// <summary> 当前槽位的等待数量 </summary>
            public int Waiters;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly object _gate = new object();

        /// <summary>
        /// 获取指定 key 的锁。返回的 Releaser 需在 using 中释放 (或显式 Dispose)。
        /// </summary>
        public async Task<Releaser> LockAsync(string key, CancellationToken cancellationToken = default)
        {
            Entry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out entry))
                {
                    entry = new Entry();
                    _entries.Add(key, entry);
                }
                entry.Waiters++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 等待被取消, 回退计数
                lock (_gate)
                {
                    entry.Waiters--;
                    if (entry.Waiters == 0)
                    {
                        _entries.Remove(key);
                    }
                }
                throw;
            }

            return new Releaser(this, key);
        }

        private void _Release(string key)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    return;
                }

                entry.Semaphore.Release();
                entry.Waiters--;
                if (entry.Waiters == 0)
                {
                    _entries.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }
    }
}
