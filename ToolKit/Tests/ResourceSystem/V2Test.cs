/*
 * 测试公共等待工具：把"最终会成立"的断言写成有上限的轮询，
 * 不用固定 Sleep 猜测调度时机。
 */

using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ToolKit.Tests.ResourceSystem
{
    internal static class V2Test
    {
        internal static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds > timeoutMilliseconds)
                {
                    throw new TimeoutException("等待条件在期限内没有成立");
                }
                await Task.Delay(5).ConfigureAwait(false);
            }
        }
    }
}
