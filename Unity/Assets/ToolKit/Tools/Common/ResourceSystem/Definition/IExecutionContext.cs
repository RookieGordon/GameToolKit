/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 执行上下文 (P0, §5.1)。注册表、ResourceEntry、PoolBucket、等待者状态
 *                只在一个 IExecutionContext 内改变。Unity 实现绑定主线程 (UnitySynchronizationContext)；
 *                内置 ImmediateExecutionContext 在锁保护下内联执行，供控制台/测试使用。
 *                规则：状态提交段不 await 外部操作；先登记状态，再启动异步操作，完成后重新投递提交。
 */

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public interface IExecutionContext
    {
        /// <summary> 当前线程是否即上下文线程 </summary>
        bool IsCurrent { get; }

        /// <summary> 错误线程立即抛出明确误用异常 </summary>
        void AssertAccess();

        /// <summary> 投递一次状态变更；可在任意线程调用 </summary>
        void Post(Action action);

        /// <summary> 在上下文内同步执行；绑定线程的实现要求调用方已经在该线程，不跨线程等待 </summary>
        void Invoke(Action action);

        /// <summary> 在上下文内同步执行并返回结果 </summary>
        T Invoke<T>(Func<T> function);

        /// <summary> 在上下文执行短操作，返回实际执行完成的任务；跨线程调用不阻塞线程 </summary>
        Task RunAsync(Action action);

        /// <summary> 在上下文执行短操作并异步取得结果；操作本身不得等待外部工作 </summary>
        Task<T> RunAsync<T>(Func<T> function);

        /// <summary> 在上下文内启动异步操作 (状态提交段内禁止调用，仅用于业务级调度) </summary>
        Task<T> InvokeAsync<T>(Func<Task<T>> function);
    }

    /// <summary>
    /// 内联执行上下文：Post/Invoke 持锁同步执行。保证多线程调用下状态变更串行化，
    /// 但等待中的异步续体仍在线程池上恢复。Unity 项目应替换为绑定主线程的实现。
    /// </summary>
    public sealed class ImmediateExecutionContext : IExecutionContext
    {
        private readonly object _gate = new object();

        public static readonly ImmediateExecutionContext Instance = new ImmediateExecutionContext();

        public bool IsCurrent => true; // 内联执行：任何调用线程都在"上下文"内 (锁保证串行)

        public void AssertAccess()
        {
        }

        public void Post(Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }
            lock (_gate)
            {
                action();
            }
        }

        public void Invoke(Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }
            lock (_gate)
            {
                action();
            }
        }

        public T Invoke<T>(Func<T> function)
        {
            if (function == null)
            {
                throw new ArgumentNullException(nameof(function));
            }
            lock (_gate)
            {
                return function();
            }
        }

        public Task RunAsync(Action action)
        {
            try
            {
                Invoke(action);
                return Task.CompletedTask;
            }
            catch (OperationCanceledException ex)
            {
                return Task.FromCanceled(_CancelledToken(ex));
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        public Task<T> RunAsync<T>(Func<T> function)
        {
            try { return Task.FromResult(Invoke(function)); }
            catch (OperationCanceledException ex) { return Task.FromCanceled<T>(_CancelledToken(ex)); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }

        private static CancellationToken _CancelledToken(OperationCanceledException exception) =>
            exception.CancellationToken.IsCancellationRequested
                ? exception.CancellationToken : new CancellationToken(canceled: true);

        public async Task<T> InvokeAsync<T>(Func<Task<T>> function)
        {
            if (function == null)
            {
                throw new ArgumentNullException(nameof(function));
            }
            // 函数体在锁内启动；其 await 的 I/O 在锁外进行，续体通过 Post/Invoke 回到锁内提交
            Task<T> task;
            lock (_gate)
            {
                task = function();
            }
            return await task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Task 等待扩展 (公开：Unity 适配层与业务也需要"可取消等待但不取消被等待操作"的模式)。
    /// </summary>
    public static class TaskWaitExtensions
    {
        /// <summary> 可被调用者令牌中断的等待：ct 触发时抛 OCE，不取消被等待的操作本身 </summary>
        public static async Task WaitWithCancellation(this Task task, CancellationToken cancellationToken)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                var done = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
                if (ReferenceEquals(done, cancelled.Task))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            await task.ConfigureAwait(false);
        }
    }
}
