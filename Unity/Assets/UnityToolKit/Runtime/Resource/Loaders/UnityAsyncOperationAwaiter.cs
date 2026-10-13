/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 在主线程观察 Unity 原生请求的实际完成。
 *                已启动请求不中断观察；结果的归属和取消后的释放由调用方统一处理。
 */

using System;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityToolKit.Runtime.Resource
{
    internal static class UnityAsyncOperationAwaiter
    {
        public static Task<T?> ObserveAsync<T>(AsyncOperation operation, Func<T?> getResult)
            where T : class
        {
            if (operation.isDone) return Task.FromResult(getResult());

            var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<AsyncOperation>? handler = null;
            handler = completed =>
            {
                completed.completed -= handler;
                try { completion.TrySetResult(getResult()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            };
            operation.completed += handler;
            return completion.Task;
        }
    }
}
