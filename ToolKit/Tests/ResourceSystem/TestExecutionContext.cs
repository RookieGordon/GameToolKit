/*
 * 测试用状态串行域：一条专用线程真正把状态变更串行化，而不是"直通"假装串行。
 * 域内调用立即执行；域外调用排队到该线程并等待实际执行完成。
 */

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class TestExecutionContext : IExecutionContext, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private readonly int _threadId;
        private int _invokeCount;

        public TestExecutionContext()
        {
            _thread = new Thread(_Run) { IsBackground = true, Name = "resource-test-context" };
            _thread.Start();
            _threadId = _thread.ManagedThreadId;
        }

        public bool IsCurrent => Thread.CurrentThread.ManagedThreadId == _threadId;

        /// <summary> 状态变更实际执行的次数 (含域内直接执行) </summary>
        public int InvokeCount => Volatile.Read(ref _invokeCount);

        public void AssertAccess()
        {
            if (IsCurrent) return;
            throw new InvalidOperationException("必须在状态串行域内访问");
        }

        public void Post(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            // 测试里让投递可观察：归还记账在返回时已经完成，不把"未记账窗口"带进断言。
            Dispatch(action, wait: true);
        }

        public void Invoke(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            Dispatch(action, wait: true);
        }

        public T Invoke<T>(Func<T> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            var result = default(T)!;
            Dispatch(() => result = function(), wait: true);
            return result;
        }

        public Task RunAsync(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return RunAsync(() => { action(); return true; });
        }

        public Task<T> RunAsync<T>(Func<T> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            if (IsCurrent) return _Inline(function);

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(() =>
                {
                    try
                    {
                        var result = function();
                        Interlocked.Increment(ref _invokeCount);
                        completion.TrySetResult(result);
                    }
                    catch (OperationCanceledException error)
                    {
                        completion.TrySetCanceled(error.CancellationToken.IsCancellationRequested
                            ? error.CancellationToken : new CancellationToken(canceled: true));
                    }
                    catch (Exception error)
                    {
                        completion.TrySetException(error);
                    }
                });
            }
            catch (InvalidOperationException)
            {
                completion.TrySetException(new ObjectDisposedException(nameof(TestExecutionContext)));
            }
            return completion.Task;
        }

        public async Task<T> InvokeAsync<T>(Func<Task<T>> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            Task<T> task;
            if (IsCurrent)
            {
                task = function();
            }
            else
            {
                var started = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _queue.Add(() => started.TrySetResult(function()));
                task = await started.Task.ConfigureAwait(false);
            }
            return await task.ConfigureAwait(false);
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
        }

        private static Task<T> _Inline<T>(Func<T> function)
        {
            try { return Task.FromResult(function()); }
            catch (OperationCanceledException error)
            {
                return Task.FromCanceled<T>(error.CancellationToken.IsCancellationRequested
                    ? error.CancellationToken : new CancellationToken(canceled: true));
            }
            catch (Exception error) { return Task.FromException<T>(error); }
        }

        private void Dispatch(Action action, bool wait)
        {
            if (IsCurrent)
            {
                action();
                Interlocked.Increment(ref _invokeCount);
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    action();
                    Interlocked.Increment(ref _invokeCount);
                    completion.TrySetResult(true);
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            });
            if (!wait) return;
            if (!completion.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException($"状态串行域在期限内没有执行该提交；IsCurrent={IsCurrent}");
            }
            completion.Task.GetAwaiter().GetResult();
        }

        private void _Run()
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                action();
            }
        }
    }
}
