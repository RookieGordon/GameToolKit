/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : Unity 主线程执行上下文 (P5, §5.1)。在主线程构造时捕获 Unity 同步上下文；
 *                Post 用于通知；RunAsync 投递并异步等待执行完成；Invoke 仅允许主线程调用。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;

namespace UnityToolKit.Runtime.Resource
{
    public sealed class UnityExecutionContext : IExecutionContext
    {
        private readonly SynchronizationContext _syncContext;
        private readonly int _threadId;

        public UnityExecutionContext()
        {
            // 必须在 Unity 主线程构造；捕获后任意线程可投递
            _syncContext = SynchronizationContext.Current
                ?? throw new InvalidOperationException("UnityExecutionContext 必须在 Unity 主线程上构造");
            _threadId = Thread.CurrentThread.ManagedThreadId;
        }

        public bool IsCurrent => Thread.CurrentThread.ManagedThreadId == _threadId;

        public void AssertAccess()
        {
            if (!IsCurrent)
            {
                throw new InvalidOperationException(
                    "当前操作必须在 Unity 主线程 (资源执行上下文) 内调用");
            }
        }

        public void Post(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            _syncContext.Post(_ => action(), null);
        }

        public void Invoke(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            AssertAccess();
            action();
        }

        public T Invoke<T>(Func<T> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            AssertAccess();
            return function();
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
                catch (OperationCanceledException ex) { return Task.FromCanceled<T>(_CancelledToken(ex)); }
                catch (Exception ex) { return Task.FromException<T>(ex); }
            }
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _syncContext.Post(_ =>
            {
                try { completion.TrySetResult(function()); }
                catch (OperationCanceledException ex) { completion.TrySetCanceled(_CancelledToken(ex)); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }, null);
            return completion.Task;
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            return RunAsync(function).Unwrap();
        }

        private static CancellationToken _CancelledToken(OperationCanceledException exception) =>
            exception.CancellationToken.IsCancellationRequested
                ? exception.CancellationToken : new CancellationToken(canceled: true);
    }
}
