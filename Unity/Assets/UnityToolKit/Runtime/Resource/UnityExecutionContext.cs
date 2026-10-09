/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : Unity 主线程执行上下文 (P5, §5.1)。在主线程构造时捕获 Unity 同步上下文；
 *                Post 投递到主线程；Invoke 在主线程时内联执行，跨线程调用时投递并等待
 *                (业务应从主线程发起调用；跨线程同步等待存在与主线程互等的理论风险)。
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

        public UnityExecutionContext()
        {
            // 必须在 Unity 主线程构造；捕获后任意线程可投递
            _syncContext = SynchronizationContext.Current
                ?? throw new InvalidOperationException("UnityExecutionContext 必须在 Unity 主线程上构造");
        }

        public bool IsCurrent => SynchronizationContext.Current == _syncContext;

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
            if (IsCurrent)
            {
                action();
                return;
            }
            var done = new ManualResetEventSlim();
            Exception? captured = null;
            _syncContext.Post(_ =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
                finally { done.Set(); }
            }, null);
            done.Wait();
            if (captured != null) throw captured;
        }

        public T Invoke<T>(Func<T> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            if (IsCurrent)
            {
                return function();
            }
            var done = new ManualResetEventSlim();
            T result = default!;
            Exception? captured = null;
            _syncContext.Post(_ =>
            {
                try { result = function(); }
                catch (Exception ex) { captured = ex; }
                finally { done.Set(); }
            }, null);
            done.Wait();
            if (captured != null) throw captured;
            return result;
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            if (IsCurrent)
            {
                return function();
            }
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _syncContext.Post(async _ =>
            {
                try { tcs.TrySetResult(await function().ConfigureAwait(false)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }, null);
            return tcs.Task;
        }
    }
}
