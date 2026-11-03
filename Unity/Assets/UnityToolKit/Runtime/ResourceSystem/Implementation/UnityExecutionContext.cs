using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;

namespace UnityToolKit.Runtime.ResourceSystem
{
    /// <summary>
    /// 绑定主线程的执行上下文。必须在主线程上创建；跨线程调用通过主线程的 SynchronizationContext 投递，
    /// 由引擎在下一帧排空，调用线程不阻塞。
    /// </summary>
    public sealed class UnityExecutionContext : IExecutionContext
    {
        private readonly SynchronizationContext _main;
        private readonly int _mainThreadId;

        public UnityExecutionContext()
        {
            _main = SynchronizationContext.Current
                    ?? throw new InvalidOperationException("UnityExecutionContext 必须在 Unity 主线程上创建");
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        public void Post(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (IsMainThread) { Guarded(action); return; }
            _main.Post(_ => Guarded(action), null);
        }

        public Task RunAsync(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return RunAsync(() => { action(); return true; });
        }

        public Task<T> RunAsync<T>(Func<T> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (IsMainThread) Complete(completion, function);
            else _main.Post(_ => Complete(completion, function), null);
            return completion.Task;
        }

        public async Task<T> InvokeAsync<T>(Func<Task<T>> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            var started = await RunAsync(function).ConfigureAwait(false);   // 同步部分在主线程启动
            return await started.ConfigureAwait(false);
        }

        private static void Complete<T>(TaskCompletionSource<T> completion, Func<T> function)
        {
            try { completion.TrySetResult(function()); }
            catch (OperationCanceledException error)
            {
                completion.TrySetCanceled(error.CancellationToken.IsCancellationRequested
                    ? error.CancellationToken : new CancellationToken(canceled: true));
            }
            catch (Exception error) { completion.TrySetException(error); }
        }

        private static void Guarded(Action action)
        {
            try { action(); }
            catch (Exception error) { Debug.LogError("[ResourceSystem] 投递的操作抛出异常：" + error); }
        }
    }
}