/*
 * description : 每个加载器的一份异步 FIFO 队列。
 *               入队只通知；消费者空闲时 await，任务到达后独立执行，不阻塞线程。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    internal sealed class LoaderQueue
    {
        private readonly Queue<LoadManager.LoadOperation> _pending = new Queue<LoadManager.LoadOperation>();
        private readonly SemaphoreSlim _available = new SemaphoreSlim(0);
        private readonly Func<LoadManager.LoadOperation, Task> _consume;
        private readonly Action<Exception> _reportUnexpectedError;
        private readonly Task[] _consumers;
        private bool _stopped;

        internal LoaderQueue(int maxConcurrentLoads, Func<LoadManager.LoadOperation, Task> consume,
            Action<Exception> reportUnexpectedError)
        {
            _consume = consume;
            _reportUnexpectedError = reportUnexpectedError;
            // 0 保持“不限制并发”的约定：一个分发循环，每份任务独立执行。
            var unlimited = maxConcurrentLoads == 0;
            _consumers = new Task[unlimited ? 1 : maxConcurrentLoads];
            for (var i = 0; i < _consumers.Length; i++)
            {
                _consumers[i] = _ConsumeAsync(unlimited);
            }
        }

        internal void Enqueue(LoadManager.LoadOperation operation)
        {
            lock (_pending)
            {
                if (_stopped) throw new ObjectDisposedException(nameof(LoaderQueue));
                _pending.Enqueue(operation);
                _available.Release();
            }
        }

        private async Task _ConsumeAsync(bool unlimited)
        {
            while (true)
            {
                await _available.WaitAsync().ConfigureAwait(false);
                LoadManager.LoadOperation operation;
                lock (_pending)
                {
                    if (_stopped) return;
                    operation = _pending.Dequeue();
                }
                if (unlimited)
                {
                    _ = _ExecuteAsync(operation);
                }
                else
                {
                    await _ExecuteAsync(operation).ConfigureAwait(false);
                }
                // 常驻消费者等待下一份任务时，不再持有上一轮解析描述与临时上下文。
                operation = null!;
            }
        }

        private async Task _ExecuteAsync(LoadManager.LoadOperation operation)
        {
            // 出队可能接续同步完成/取消回调；后端不得嵌入生产者或旧回调的调用栈。
            await Task.Yield();
            try
            {
                await _consume(operation).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _reportUnexpectedError(error);
            }
        }

        /// <summary>请求由 LoadManager 先行取消；这里只唤醒消费者退出，不等待后端或业务引用。</summary>
        internal void Stop()
        {
            lock (_pending)
            {
                if (_stopped) return;
                _stopped = true;
                _pending.Clear();
                _available.Release(_consumers.Length);
            }
            _ = _DisposeSignalAsync();
        }

        private async Task _DisposeSignalAsync()
        {
            // 有限消费者在当前加载实际结束后退出；不限分发的执行任务不再访问队列。
            await Task.WhenAll(_consumers).ConfigureAwait(false);
            _available.Dispose();
        }
    }
}
