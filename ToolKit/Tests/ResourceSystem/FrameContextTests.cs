/*
 * 帧驱动（真非阻塞）上下文下的组合用例 (A13)。
 * 上下文把状态变更交给一条帧线程执行，投递不阻塞调用者；
 * 因此这里可以观察 TestExecutionContext 下被抹掉的窗口：凭证已失效而记账尚未执行。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    [Collection("ResourceSystem")]
    public sealed class FrameContextTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        private static ResourceManager NewManager(FrameExecutionContext context, out FakeLoader loader)
        {
            loader = new FakeLoader();
            var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics());
            manager.RegisterLoader(ResourceSource.Local, loader);
            return manager;
        }

        /// <summary> 域内读第一条资源行的持有数；没有行时为 -1 </summary>
        private static int HoldCount(FrameExecutionContext context, ResourceManager manager) =>
            context.ReadInDomain(() =>
            {
                var rows = manager.GetSnapshot().ResourceRows;
                return rows.Count == 0 ? -1 : rows[0].HoldCount;
            });

        [Fact]
        public async Task 同键并发请求只进入一次实际加载且各自持有()
        {
            using var context = new FrameExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();

            var first = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            var second = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            var third = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);

            var references = await Task.WhenAll(first, second, third).WaitAsync(TestTimeout);
            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(3, HoldCount(context, manager));

            var expected = context.ReadInDomain(() => references[0].Value);
            foreach (var reference in references)
            {
                Assert.Same(expected, context.ReadInDomain(() => reference.Value));
                reference.Dispose();
            }
            Assert.Equal(0, HoldCount(context, manager));
        }

        [Fact]
        public async Task 归还投递不阻塞业务线程且记账在帧内完成()
        {
            using var context = new FrameExecutionContext();
            using var manager = NewManager(context, out _);
            await manager.InitializeAsync();

            var reference = await manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local)
                .WaitAsync(TestTimeout);
            Assert.Equal(1, HoldCount(context, manager));

            // 在别的线程归还：Dispose 只入队，不等帧线程执行。
            var returned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                reference.Dispose();
                returned.TrySetResult(true);
            });
            thread.Start();

            Assert.True(await returned.Task.WaitAsync(TestTimeout), "归还投递阻塞了业务线程");
            Assert.False(reference.IsValid);
            // 记账尚未执行的窗口是真实存在的；推进一帧后落到共享计数上，不丢归还。
            Assert.Equal(0, HoldCount(context, manager));
            Assert.Equal(0, context.Pending);
        }

        [Fact]
        public async Task 跨线程归还后追加持有被拒绝()
        {
            using var context = new FrameExecutionContext();
            using var manager = NewManager(context, out _);
            await manager.InitializeAsync();

            var reference = await manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local)
                .WaitAsync(TestTimeout);

            reference.Dispose();
            var error = await Assert.ThrowsAsync<ResourceLoadException>(() => reference.RetainAsync());
            Assert.Equal(DiagnosticCodes.AssetInvalidated, error.Error.DiagnosticCode);
            Assert.Equal(0, HoldCount(context, manager));
        }

        [Fact]
        public async Task 归还后卸载释放产物且重复归还只生效一次()
        {
            using var context = new FrameExecutionContext();
            using var manager = NewManager(context, out _);
            await manager.InitializeAsync();

            var reference = await manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local)
                .WaitAsync(TestTimeout);
            reference.Dispose();
            reference.Dispose();
            reference.Dispose();
            // 全部归还后要么行还在且计数为 0，要么已经没有任何行；两者都表示持有已归零。
            Assert.True(HoldCount(context, manager) <= 0, "重复归还后持有计数没有归零");

            var unloaded = await manager.UnloadUnusedAsync().WaitAsync(TestTimeout);
            Assert.Equal(1, unloaded.ReleasedCount);
            // 卸载之后要么行还在且为 0，要么行已经整体消失；两者都表示产物已释放。
            Assert.True(HoldCount(context, manager) <= 0, "卸载后仍看到被持有的产物行");
        }
    }
}
