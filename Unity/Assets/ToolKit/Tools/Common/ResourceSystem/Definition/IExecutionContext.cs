/*
 * description  : 执行上下文：引擎调用 (转换、实例化、重置、销毁) 所在的地方。
 *                核心的状态自己用锁串行，不依赖它；它只负责"把一小段引擎调用送到正确的线程并拿回结果"。
 *                Unity 项目用 UnityToolKit 里绑定主线程的实现；控制台/测试用内联实现。
 */

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public interface IExecutionContext
    {
        /// <summary> 投递一段不需要等待结果的短操作；任意线程可调，不阻塞调用线程 </summary>
        void Post(Action action);

        /// <summary> 在上下文内执行短操作并等待完成；跨线程调用不阻塞调用线程 </summary>
        Task RunAsync(Action action);

        /// <summary> 在上下文内执行短操作并异步取得结果；操作本身不得等待外部工作 </summary>
        Task<T> RunAsync<T>(Func<T> function);

        /// <summary> 在上下文内启动一个异步操作 (如 Resources.LoadAsync 轮询) 并等它完成 </summary>
        Task<T> InvokeAsync<T>(Func<Task<T>> function);
    }

    /// <summary>
    /// 内联执行上下文：在调用线程上直接执行，持锁保证并发调用串行。
    /// </summary>
    public sealed class ImmediateExecutionContext : IExecutionContext
    {
        private readonly object _gate = new object();

        public static readonly ImmediateExecutionContext Instance = new ImmediateExecutionContext();

        public void Post(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            lock (_gate) action();
        }

        public Task RunAsync(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return RunAsync(() => { action(); return true; });
        }

        public Task<T> RunAsync<T>(Func<T> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            try
            {
                lock (_gate) return Task.FromResult(function());
            }
            catch (OperationCanceledException error)
            {
                return Task.FromCanceled<T>(error.CancellationToken.IsCancellationRequested
                    ? error.CancellationToken : new CancellationToken(canceled: true));
            }
            catch (Exception error)
            {
                return Task.FromException<T>(error);
            }
        }

        public async Task<T> InvokeAsync<T>(Func<Task<T>> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            Task<T> task;
            lock (_gate) task = function();          // 同步部分在锁内启动；其后的等待在锁外
            return await task.ConfigureAwait(false);
        }
    }
}
