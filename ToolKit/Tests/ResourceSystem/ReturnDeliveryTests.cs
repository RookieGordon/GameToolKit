/*
 * 归还投递的非阻塞验证 (A13 的可测部分)。契约要求：归还投递不阻塞业务线程。
 * TestExecutionContext 的状态串行域是真实线程：如果 ResourceRef.Dispose 在调用者线程上等待记账，
 * 归还线程就会被串行域挡住。这里用真实异步上下文直接测量归还线程的结束时间。
 * 说明：显式排空的"Draining"上下文没有纳入测试集——它需要引擎式帧循环才能稳定驱动，
 * 本环境无法验证，因此不以未验证的测试装置冒充覆盖。
 */

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    [Collection("ResourceSystem")]
    public sealed class ReturnDeliveryTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task 归还投递不阻塞业务线程且记账随后完成()
        {
            using var context = new TestExecutionContext();
            var loader = new FakeLoader();
            using var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics());
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var reference = await manager.LoadAsync<FakeProduct>("delivery", ResourceSource.Local)
                .WaitAsync(TestTimeout);
            Assert.Equal(1, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);

            // 在别的线程归还：Dispose 只投递，不等待状态串行域完成记账。
            var watch = Stopwatch.StartNew();
            var thread = new Thread(() => reference.Dispose());
            thread.Start();
            var joined = thread.Join(TestTimeout);
            watch.Stop();

            Assert.True(joined, "归还线程在期限内没有结束：归还投递阻塞了业务线程");
            Assert.False(reference.IsValid);
            // 记账仍然发生，不丢归还。
            await V2Test.WaitUntilAsync(() => manager.GetSnapshot().ResourceRows[0].HoldCount == 0);
            Assert.Equal(0, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);
        }

        [Fact]
        public async Task 重复归还不改变持有计数()
        {
            using var context = new TestExecutionContext();
            var loader = new FakeLoader();
            using var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics());
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var reference = await manager.LoadAsync<FakeProduct>("delivery", ResourceSource.Local)
                .WaitAsync(TestTimeout);

            reference.Dispose();
            reference.Dispose();
            reference.Dispose();

            await V2Test.WaitUntilAsync(() => manager.GetSnapshot().ResourceRows[0].HoldCount == 0);
            Assert.Equal(0, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);
        }
    }
}
