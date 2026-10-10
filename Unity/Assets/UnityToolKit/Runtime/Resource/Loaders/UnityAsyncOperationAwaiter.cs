/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : Unity 异步操作的观察工具 (R03)。Unity 原生请求不可中断：
 *                调用者取消只结束包装等待；操作必须观察到真实完成，迟到对象由
 *                disposeLate 回收回收入口，不允许失去所有者。注销 registration 于 finally。
 */

using System;
using System.Threading;
using ToolKit.Tools.Common;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityToolKit.Runtime.Resource
{
    internal static class UnityAsyncOperationAwaiter
    {
        public static async Task<T?> ObserveAsync<T>(
            AsyncOperation operation, Func<T?> getResult, Action<T?>? disposeLate,
            CancellationToken ct, IExecutionContext context)
            where T : class
        {
            var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<AsyncOperation>? handler = null;
            handler = op =>
            {
                op.completed -= handler;
                try { tcs.TrySetResult(getResult()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            };
            operation.completed += handler;
            try
            {
                // 取消只中断等待；返回结果仍使用异步任务，不引入同步等待。
                await tcs.Task.WaitWithCancellation(ct).ConfigureAwait(false);
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 等待已取消，但底层请求仍在飞行：等待真实完成并回收迟到结果 (R03)
                T? late = null;
                try
                {
                    late = await tcs.Task.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 完成回调自身的异常不再掩盖原始取消
                }
                try
                {
                    if (disposeLate != null)
                    {
                        await context.RunAsync(() => disposeLate(late)).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    // 迟到回收尽力而为
                }
                throw;
            }
        }
    }
}
