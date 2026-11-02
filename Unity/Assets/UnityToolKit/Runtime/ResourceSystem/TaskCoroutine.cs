/*
 * description  : async/await 与 Unity 协程的桥接。
 *                TaskYield 把 Task 包成 CustomYieldInstruction：协程里 yield return 它，完成后读 Result 或 Exception；
 *                ToCoroutine 把 Task 变成可 StartCoroutine 的 IEnumerator，失败走回调或 Debug.LogException。
 *                只在主线程使用；它不取消 Task，StopCoroutine 只是不再观察结果。
 */

using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityToolKit.Runtime.ResourceSystem
{
    /// <summary> 等待一个 Task 的协程指令；不抛异常，由调用方在 yield 之后检查状态 </summary>
    public class TaskYield : CustomYieldInstruction
    {
        public TaskYield(Task task)
        {
            Task = task ?? throw new ArgumentNullException(nameof(task));
        }

        public Task Task { get; }

        public override bool keepWaiting => !Task.IsCompleted;

        public bool IsCompleted => Task.IsCompleted;
        public bool IsSucceeded => Task.Status == TaskStatus.RanToCompletion;
        public bool IsFaulted => Task.IsFaulted;
        public bool IsCanceled => Task.IsCanceled;

        /// <summary> 失败原因 (已剥掉 AggregateException)；取消时是 TaskCanceledException；成功或未完成时为 null </summary>
        public Exception Exception
        {
            get
            {
                if (Task.IsFaulted) return Task.Exception?.Flatten().InnerExceptions[0] ?? Task.Exception;
                if (Task.IsCanceled) return new TaskCanceledException(Task);
                return null;
            }
        }

        /// <summary> 失败或取消时重新抛出原异常；成功时无事发生 </summary>
        public void ThrowIfFailed()
        {
            if (!Task.IsCompleted) throw new InvalidOperationException("Task 尚未完成");
            if (Task.IsFaulted || Task.IsCanceled) Task.GetAwaiter().GetResult();
        }
    }

    /// <summary> 带结果的版本：成功后从 Result 读取 </summary>
    public sealed class TaskYield<T> : TaskYield
    {
        private readonly Task<T> _task;

        public TaskYield(Task<T> task) : base(task)
        {
            _task = task;
        }

        /// <summary> 成功结果；未完成、失败或取消时抛出 (失败抛原异常) </summary>
        public T Result
        {
            get
            {
                ThrowIfFailed();
                return _task.Result;
            }
        }

        /// <summary> 成功时取结果并返回 true；否则返回 false，结果为 default </summary>
        public bool TryGetResult(out T result)
        {
            if (IsSucceeded)
            {
                result = _task.Result;
                return true;
            }
            result = default;
            return false;
        }
    }

    public static class TaskCoroutineExtensions
    {
        /// <summary> 在协程里等待：yield return task.AsYield(); 然后检查 IsSucceeded / Exception </summary>
        public static TaskYield AsYield(this Task task) => new TaskYield(task);

        /// <summary> 在协程里等待并取结果：var load = task.AsYield(); yield return load; load.Result </summary>
        public static TaskYield<T> AsYield<T>(this Task<T> task) => new TaskYield<T>(task);

        /// <summary>
        /// 把 Task 变成可 StartCoroutine 的协程。完成后按结果回调；失败且没有 onError 时 Debug.LogException。
        /// 协程被 Stop 只是不再观察，Task 本身继续执行。
        /// </summary>
        public static IEnumerator ToCoroutine(this Task task, Action onCompleted = null, Action<Exception> onError = null)
        {
            var waiting = new TaskYield(task);
            yield return waiting;
            if (waiting.IsSucceeded) onCompleted?.Invoke();
            else Report(waiting.Exception, onError);
        }

        public static IEnumerator ToCoroutine<T>(this Task<T> task, Action<T> onCompleted, Action<Exception> onError = null)
        {
            var waiting = new TaskYield<T>(task);
            yield return waiting;
            if (waiting.TryGetResult(out var result)) onCompleted?.Invoke(result);
            else Report(waiting.Exception, onError);
        }

        private static void Report(Exception error, Action<Exception> onError)
        {
            if (onError != null) onError(error);
            else if (!(error is OperationCanceledException)) Debug.LogException(error);
        }
    }
}
