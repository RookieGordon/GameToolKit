/*
 * 测试用真非阻塞状态串行域：一条专用线程持有串行域，帧内按接收顺序执行状态变更。
 * 与 TestExecutionContext 的区别：后者把投递同步等完，会把"晚归还窗口"从测试里抹掉。
 * 这里投递只入队、不阻塞，由帧循环执行；测试可以显式推进一帧，从而让
 * "凭证已失效但记账尚未执行" 的窗口真实存在，用来验证归还投递不阻塞业务线程与跨线程操作。
 */

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class FrameExecutionContext : IExecutionContext, IDisposable
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private readonly SemaphoreSlim _frame = new SemaphoreSlim(0);
        private readonly Thread _driver;
        private readonly int _driverId;
        private int _disposed;

        public FrameExecutionContext()
        {
            _driver = new Thread(_Run) { IsBackground = true, Name = "resource-frame-context" };
            _driver.Start();
            _driverId = _driver.ManagedThreadId;
        }

        public bool IsCurrent => Thread.CurrentThread.ManagedThreadId == _driverId;

        /// <summary> 当前排队未执行的投递数 </summary>
        public int Pending => _queue.Count;

        public void AssertAccess()
        {
            if (IsCurrent) return;
            throw new InvalidOperationException("必须在状态串行域内访问");
        }

        /// <summary> 投递即走：绝不在调用者线程上等待执行 </summary>
        public void Post(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            _queue.Enqueue(action);
        }

        public void Invoke(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            Invoke(() => { action(); return true; });
        }

        /// <summary> 同步执行：域内直接执行；域外投递并等待，由帧循环完成 </summary>
        public T Invoke<T>(Func<T> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            if (IsCurrent) return function();

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(() =>
            {
                try { completion.TrySetResult(function()); }
                catch (Exception error) { completion.TrySetException(error); }
            });
            // 由调用者推进一帧来完成这次读取，不依赖测试何时调用 Tick。
            AdvanceFrame();
            if (!completion.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("状态串行域没有在期限内执行这次读取");
            }
            return completion.Task.GetAwaiter().GetResult();
        }

        public Task RunAsync(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return RunAsync(() => { action(); return true; });
        }

        public Task<T> RunAsync<T>(Func<T> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            if (IsCurrent)
            {
                try { return Task.FromResult(function()); }
                catch (OperationCanceledException error)
                {
                    return Task.FromCanceled<T>(error.CancellationToken.IsCancellationRequested
                        ? error.CancellationToken : new CancellationToken(canceled: true));
                }
                catch (Exception error) { return Task.FromException<T>(error); }
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(() =>
            {
                try { completion.TrySetResult(function()); }
                catch (OperationCanceledException error)
                {
                    completion.TrySetCanceled(error.CancellationToken.IsCancellationRequested
                        ? error.CancellationToken : new CancellationToken(canceled: true));
                }
                catch (Exception error) { completion.TrySetException(error); }
            });
            return completion.Task;
        }

        public async Task<T> InvokeAsync<T>(Func<Task<T>> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            Task<T> started;
            if (IsCurrent)
            {
                started = function();
            }
            else
            {
                var slot = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _queue.Enqueue(() =>
                {
                    try { slot.TrySetResult(function()); }
                    catch (Exception error) { slot.TrySetException(error); }
                });
                AdvanceFrame();
                started = await slot.Task.ConfigureAwait(false);
            }
            return await started.ConfigureAwait(false);
        }

        /// <summary> 推进一帧：等待帧线程把当前排队的投递执行完 </summary>
        public void AdvanceFrame()
        {
            if (_disposed != 0) return;
            var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(() => drained.TrySetResult(true));
            _frame.Release();
            if (!drained.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("帧线程在期限内没有排空投递");
            }
        }

        /// <summary> 在域内读取引擎侧状态 (如快照)，必要时先推进一帧 </summary>
        public T ReadInDomain<T>(Func<T> read) => Invoke(read);

        /// <summary> 推进若干帧直到条件在域内成立 </summary>
        public async Task<bool> AdvanceUntilAsync(Func<bool> condition, int frames = 200)
        {
            for (var i = 0; i < frames; i++)
            {
                if (Invoke(condition)) return true;
                await Task.Delay(1).ConfigureAwait(false);
            }
            return Invoke(condition);
        }

        /// <summary> 帧线程：像引擎主循环一样不断排空投递队列 </summary>
        private void _Run()
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                _frame.Wait(5);
                while (_queue.TryDequeue(out var action))
                {
                    try { action(); }
                    catch (Exception) { /* 状态提交抛错由调用者通过完成源观察到 */ }
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _frame.Release();
            _driver.Join(TimeSpan.FromSeconds(2));
            _frame.Dispose();
        }
    }
}
