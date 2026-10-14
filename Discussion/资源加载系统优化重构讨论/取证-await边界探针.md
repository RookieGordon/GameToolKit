# await 边界探针（讨论取证，非产品代码）

> 用途：验证"`await` 是否构成外部代码的执行边界"这一争论点。
> **不落地到生产或测试工程**：本轮只做讨论和文档，按 GPT 第 4 轮意见，该文件已从 `ToolKit\Tests\ResourceSystem\` 删除，基线恢复 214。
> 要复核时把下面代码贴回 `ToolKit\Tests\ResourceSystem\AwaitBoundaryProbeTests.cs` 即可复现（Release 下 4 个用例全绿）。

## 实测结论

| # | 用例 | 观察到的事实 |
|---|---|---|
| 1 | `SemaphoreWaitAsync_WhenFree_RunsBackendInlineOnCallerStack` | 额度空闲时 `WaitAsync` 同步完成；调用者返回**之前**已打印 `backend:begin`。**`await` 不是边界**，GPT 第 2 轮判断成立。 |
| 2 | `SynchronousBackendWithoutYield_CompletesEntirelyOnCallerStack` | 同步完成的后端 + 不显式让出：整段加载在调用者栈上跑完，`producer:return taskDone=True`。 |
| 3 | `TaskYield_MovesBackendOffCallerStack` | 显式让出后，调用者返回时任务未完成，后端不在其栈上。 |
| 4 | `KeepingTaskRecordUntilBackendEnds_PreventsOverlap` | 记录留到后端真正结束：同键只登记一份；失败后新建记录；并发峰值恒为 1（不靠 `IsAbandoned` 字段）。 |

## 明确的适用前提（按 GPT 第 4 轮的边界要求补正）

- 用例 1~3 在**无同步上下文**的线程池上、且续体使用 `ConfigureAwait(false)` 时成立。
- 因此可验证的契约是：**"外部代码不在我们的状态修改栈内开始"**。
- **"调用者返回时后端尚未开始"是更强的时序要求**，在 Unity 主线程同步上下文下不能由默认调度直接推出，必须单独验证，不能与前者画等号。
- 用例 2 说明：这条更强要求**必须靠显式让出**，不能靠"写了 await"。

## 探针源码

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public class AwaitBoundaryProbeTests
    {
        [Fact]
        public void SemaphoreWaitAsync_WhenFree_RunsBackendInlineOnCallerStack()
        {
            var gate = new SemaphoreSlim(1);
            var log = new System.Collections.Generic.List<string>();

            void Producer()
            {
                log.Add("producer:enter");
                var t = RunAsync(gate, log);
                log.Add($"producer:return taskDone={t.IsCompleted}");
            }

            Producer();

            Assert.Equal(5, log.Count);
            Assert.Equal("producer:enter", log[0]);
            Assert.Equal("commit:begin", log[1]);
            Assert.Equal("commit:end", log[2]);
            Assert.Equal("backend:begin", log[3]);            // 已在生产者栈上执行
            Assert.Equal("producer:return taskDone=False", log[4]);

            gate.Release();
            SpinWait.SpinUntil(() => log.Count >= 6, TimeSpan.FromSeconds(2));
            Assert.Equal("backend:end", log[5]);
        }

        private static async Task RunAsync(SemaphoreSlim gate, System.Collections.Generic.List<string> log)
        {
            log.Add("commit:begin");
            log.Add("commit:end");
            await gate.WaitAsync().ConfigureAwait(false);
            log.Add("backend:begin");
            await Task.Yield();
            log.Add("backend:end");
        }

        [Fact]
        public void SynchronousBackendWithoutYield_CompletesEntirelyOnCallerStack()
        {
            var log = new System.Collections.Generic.List<string>();

            void Producer()
            {
                log.Add("producer:enter");
                var t = RunSyncBackendAsync(log);
                log.Add($"producer:return taskDone={t.IsCompleted}");
            }

            Producer();

            Assert.Equal(new[]
            {
                "producer:enter", "commit:begin", "commit:end",
                "backend:begin", "backend:end",
                "producer:return taskDone=True",
            }, log);
        }

        private static async Task RunSyncBackendAsync(System.Collections.Generic.List<string> log)
        {
            log.Add("commit:begin");
            log.Add("commit:end");
            await Task.CompletedTask.ConfigureAwait(false);
            log.Add("backend:begin");
            log.Add("backend:end");
        }

        [Fact]
        public void TaskYield_MovesBackendOffCallerStack()
        {
            var log = new System.Collections.Generic.List<string>();
            var gate = new SemaphoreSlim(1);

            void Producer()
            {
                log.Add("producer:enter");
                var t = RunWithYieldAsync(gate, log);
                log.Add($"producer:return taskDone={t.IsCompleted}");
            }

            Producer();

            Assert.Equal("producer:enter", log[0]);
            Assert.Equal("producer:return taskDone=False", log[1]);
        }

        private static async Task RunWithYieldAsync(SemaphoreSlim gate, System.Collections.Generic.List<string> log)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            await Task.Yield();
            log.Add("backend:begin");
        }

        [Fact]
        public void KeepingTaskRecordUntilBackendEnds_PreventsOverlap()
        {
            var pipeline = new InFlightPipeline();
            var backendRunning = 0;
            var peakOverlap = 0;
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<object> BackendAsync(CancellationToken ct)
            {
                var now = Interlocked.Increment(ref backendRunning);
                peakOverlap = Math.Max(peakOverlap, now);
                await release.Task;
                Interlocked.Decrement(ref backendRunning);
                throw new InvalidOperationException("backend failed");
            }

            var first = pipeline.LoadAsync("k", BackendAsync, CancellationToken.None);
            var second = pipeline.LoadAsync("k", BackendAsync, CancellationToken.None);
            Assert.Same(first.Record, second.Record);   // 同键只登记一份

            release.SetResult(true);
            Assert.ThrowsAsync<InvalidOperationException>(() => first.Completion).GetAwaiter().GetResult();
            Assert.ThrowsAsync<InvalidOperationException>(() => second.Completion).GetAwaiter().GetResult();

            var third = pipeline.LoadAsync("k", BackendAsync, CancellationToken.None);
            Assert.NotSame(first.Record, third.Record);  // 失败后新建记录
            Assert.Equal(1, peakOverlap);                // 从未重叠
        }
    }

    /// <summary>讨论用最小流水线骨架：在途表 + 单一记录 + 终结后保留到后端真正结束。</summary>
    internal sealed class InFlightPipeline
    {
        private readonly System.Collections.Generic.Dictionary<string, Record> _inFlight =
            new System.Collections.Generic.Dictionary<string, Record>();

        internal sealed class Record
        {
            public TaskCompletionSource<object> Completion { get; } =
                new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal (Record Record, Task<object> Completion) LoadAsync(
            string key, Func<CancellationToken, Task<object>> backend, CancellationToken ct)
        {
            if (_inFlight.TryGetValue(key, out var existing))
                return (existing, existing.Completion.Task);

            var record = new Record();
            _inFlight[key] = record;
            _ = RunAsync(record, key, backend, ct);
            return (record, record.Completion.Task);
        }

        private async Task RunAsync(Record record, string key, Func<CancellationToken, Task<object>> backend, CancellationToken ct)
        {
            try
            {
                var result = await backend(ct).ConfigureAwait(false);
                record.Completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                record.Completion.TrySetException(ex);
            }
            finally
            {
                _inFlight.Remove(key); // 直到后端真正结束才移除登记
            }
        }
    }
}
```
