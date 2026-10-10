using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 按键异步取得执行权。同键按到达顺序执行，不同键独立执行。
    /// 等待者通过任务通知继续运行，不占用线程等待。业务结果由调用者保存、复查。
    /// </summary>
    public sealed class KeyedAsyncLock<TKey> where TKey : notnull
    {
        /// <summary>一次取得执行权的凭证。可重复释放，但只会放行一个后续请求。</summary>
        public sealed class Releaser : IDisposable
        {
            private readonly KeyedAsyncLock<TKey> _owner;
            internal readonly Entry Entry;
            internal bool Released;

            internal Releaser(KeyedAsyncLock<TKey> owner, Entry entry)
            {
                _owner = owner;
                Entry = entry;
            }

            /// <summary>
            /// 让当前已经排队的请求以同一个异常结束。以后到达的请求仍可重试。
            /// 此操作不释放执行权；调用者必须完成清理后再 Dispose。
            /// 适用于本轮操作失败已能判定其他等待请求也无法成功的场景。
            /// </summary>
            public void FailWaitingRequests(Exception error)
            {
                if (error == null) throw new ArgumentNullException(nameof(error));
                _owner.FailWaitingRequests(this, error);
            }

            public void Dispose() => _owner.Release(this);
        }

        internal sealed class Entry
        {
            public readonly TKey Key;
            public readonly LinkedList<WaitingRequest> Waiting = new LinkedList<WaitingRequest>();
            public Entry(TKey key) => Key = key;
        }

        internal sealed class WaitingRequest
        {
            public readonly TaskCompletionSource<Releaser> Completion =
                new TaskCompletionSource<Releaser>(TaskCreationOptions.RunContinuationsAsynchronously);
            public LinkedListNode<WaitingRequest>? Node;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<TKey, Entry> _entries = new Dictionary<TKey, Entry>();

        /// <summary>
        /// 异步取得 key 的执行权。取得后须用 using 或 Dispose 释放。
        /// 取消仅退出本次等待，不取消持有者或其他请求；已取得的执行权由调用者负责释放。
        /// </summary>
        public Task<Releaser> LockAsync(TKey key, CancellationToken cancellationToken = default)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<Releaser>(cancellationToken);

            WaitingRequest request;
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    entry = new Entry(key);
                    _entries.Add(key, entry);
                    return Task.FromResult(new Releaser(this, entry));
                }

                request = new WaitingRequest();
                request.Node = entry.Waiting.AddLast(request);
            }

            return WaitAsync(request, cancellationToken);
        }

        private async Task<Releaser> WaitAsync(WaitingRequest request, CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(() => CancelWaitingRequest(request, cancellationToken)))
                return await request.Completion.Task.ConfigureAwait(false);
        }

        private void CancelWaitingRequest(WaitingRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                // 已被放行或失败通知移出队列时，不再撤销其结果。
                var queue = request.Node!.List;
                if (queue == null) return;
                queue.Remove(request.Node);
                request.Completion.TrySetCanceled(cancellationToken);
            }
        }

        private void FailWaitingRequests(Releaser holder, Exception error)
        {
            lock (_gate)
            {
                if (holder.Released) return;
                var queue = holder.Entry.Waiting;
                while (queue.First != null)
                {
                    var request = queue.First.Value;
                    queue.RemoveFirst();
                    request.Completion.TrySetException(error);
                }
            }
        }

        private void Release(Releaser holder)
        {
            lock (_gate)
            {
                if (holder.Released) return;
                holder.Released = true;
                var entry = holder.Entry;
                if (entry.Waiting.First == null)
                {
                    _entries.Remove(entry.Key);
                    return;
                }

                var next = entry.Waiting.First.Value;
                entry.Waiting.RemoveFirst();
                next.Completion.TrySetResult(new Releaser(this, entry));
            }
        }
    }

    /// <summary>使用字符串作为键的兼容入口。</summary>
    public sealed class KeyedAsyncLock
    {
        private readonly KeyedAsyncLock<string> _locks = new KeyedAsyncLock<string>();

        /// <summary>复制此结构后重复 Dispose 仍只会释放一次执行权。</summary>
        public readonly struct Releaser : IDisposable
        {
            private readonly KeyedAsyncLock<string>.Releaser? _holder;

            internal Releaser(KeyedAsyncLock<string>.Releaser holder) => _holder = holder;

            public void FailWaitingRequests(Exception error)
            {
                if (error == null) throw new ArgumentNullException(nameof(error));
                _holder?.FailWaitingRequests(error);
            }

            public void Dispose() => _holder?.Dispose();
        }

        public async Task<Releaser> LockAsync(string key, CancellationToken cancellationToken = default)
        {
            return new Releaser(await _locks.LockAsync(key, cancellationToken).ConfigureAwait(false));
        }
    }
}
