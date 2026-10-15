/*
 * 实例池行为用例：原型持有、闲置复用、每桶一次创建、归还与销毁、配额与清池。
 * 断言用可直接观察的契约：创建/销毁次数、实例身份、池行计数与原型是否仍可复用。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    [Collection("ResourceSystem")]
    public sealed class InstancePoolTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        private static ResourceManager NewManager(
            TestExecutionContext context, out FakeLoader loader, out FakeInstanceFactory factory,
            PoolPolicy? policy = null)
        {
            loader = new FakeLoader();
            factory = new FakeInstanceFactory();
            var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics());
            manager.RegisterLoader(ResourceSource.Local, loader);
            manager.RegisterFactory("instances", factory, policy);
            return manager;
        }

        private static T ReadLease<T>(TestExecutionContext context, InstanceLease<T> lease) where T : class =>
            context.Invoke(() => lease.Value);

        [Fact]
        public async Task 首次租用加载原型并创建一次实例()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader, out var factory);
            await manager.InitializeAsync();

            using var lease = await manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances")
                .WaitAsync(TestTimeout);

            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(1, factory.CreateCount);
            Assert.True(ReadLease(context, lease).RentReset);

            var row = Assert.Single(manager.GetSnapshot().PoolRows);
            Assert.Equal(1, row.Active);
            Assert.Equal(0, row.Idle);
        }

        [Fact]
        public async Task 归还后闲置再租用复用同一实例且不重复加载原型()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader, out var factory);
            await manager.InitializeAsync();

            var first = await manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances")
                .WaitAsync(TestTimeout);
            var instance = ReadLease(context, first);
            first.Dispose();

            var idleRow = Assert.Single(manager.GetSnapshot().PoolRows);
            Assert.Equal(0, idleRow.Active);
            Assert.Equal(1, idleRow.Idle);

            using var second = await RentOrReport(
                manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances"), manager)
                .WaitAsync(TestTimeout);
            Assert.Same(instance, ReadLease(context, second));
            // 归还重置与租用重置都在各自时机发生。
            Assert.True(instance.ReturnReset, "ReturnReset 未发生；" + DescribeLastError(manager));
            Assert.True(instance.RentReset);
            Assert.Equal(1, factory.CreateCount);
            Assert.Equal(1, loader.LoadCount);
        }

        private static string DescribeLastError(ResourceManager manager)
        {
            var snapshot = manager.GetSnapshot();
            return string.Join(";", snapshot.DirtyResources.ConvertAll(d => d.Error.ToString()));
        }

        private static async Task<InstanceLease<T>> RentOrReport<T>(
            Task<InstanceLease<T>> task, ResourceManager manager) where T : class
        {
            try
            {
                return await task;
            }
            catch (ResourceLoadException error)
            {
                throw new InvalidOperationException(
                    error.Error.DiagnosticCode + " " +
                    string.Join(",", error.Error.Context.Select(p => p.Key + "=" + p.Value)), error);
            }
        }
        [Fact]
        public async Task 闲置上限为零时归还即销毁且桶空后立即归还原型()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out _, out var factory,
                new PoolPolicy { MaxIdlePerResource = 0, IdleLifetime = TimeSpan.FromMinutes(10) });
            await manager.InitializeAsync();

            var lease = await manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances")
                .WaitAsync(TestTimeout);
            var instance = ReadLease(context, lease);
            lease.Dispose();

            await V2Test.WaitUntilAsync(() => instance.Destroyed);
            Assert.Equal(1, factory.DestroyCount);
            // 桶不按时间驻留：没有闲置、没有在用，桶立即退出而不是等闲置寿命到期。
            await V2Test.WaitUntilAsync(() => manager.GetSnapshot().PoolRows.Count == 0);
        }

        [Fact]
        public async Task 没有闲置时多个租用各自创建不在池内排队()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out _, out var factory,
                new PoolPolicy { IdleLifetime = TimeSpan.FromMinutes(10) });
            await manager.InitializeAsync();

            var first = await manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances")
                .WaitAsync(TestTimeout);
            var firstInstance = ReadLease(context, first);
            Assert.Equal(1, factory.CreateCount);

            // 池不排队也不按额度分配：没有闲置就直接创建第二个实例。
            using var second = await manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances")
                .WaitAsync(TestTimeout);
            Assert.Equal(2, factory.CreateCount);
            Assert.NotSame(firstInstance, ReadLease(context, second));
        }

        [Fact]
        public async Task 归还的实例优先复用而不是再次创建()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out _, out var factory,
                new PoolPolicy { IdleLifetime = TimeSpan.FromMinutes(10) });
            await manager.InitializeAsync();

            var first = await manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances")
                .WaitAsync(TestTimeout);
            var firstInstance = ReadLease(context, first);
            first.Dispose();

            using var second = await manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances")
                .WaitAsync(TestTimeout);
            Assert.Equal(1, factory.CreateCount);
            Assert.Same(firstInstance, ReadLease(context, second));
        }

        [Fact]
        public async Task 创建未完成即超时的租用有终态且迟到实例不进入闲置()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out _, out var factory,
                new PoolPolicy { IdleLifetime = TimeSpan.FromMinutes(10) });
            await manager.InitializeAsync();

            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 创建比本次租用的预算慢：预算到期时创建仍在进行，迟到结果必须被收尾。
            factory.OnCreate = async (_, _) =>
            {
                started.TrySetResult(true);
                await Task.Delay(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
                return new FakeInstance(1);
            };

            var options = new RequestOptions { Timeout = TimeSpan.FromMilliseconds(50) };
            var rent = manager.RentAsync<FakeInstance>(
                "asset", ResourceSource.Local, "instances", options);
            await started.Task.WaitAsync(TestTimeout);
            await Assert.ThrowsAnyAsync<ResourceLoadException>(() => rent.WaitAsync(TestTimeout));

            // 迟到的实例必须被销毁，而不是悄悄留在闲置队列里。
            await V2Test.WaitUntilAsync(() => factory.DestroyCount == 1);
            await V2Test.WaitUntilAsync(() => manager.GetSnapshot().PoolRows.Count == 0);
        }

        [Fact]
        public async Task 存活查询异常不等于确认销毁且不产生销毁调用()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out _, out var factory,
                new PoolPolicy { IdleLifetime = TimeSpan.FromMinutes(10) });
            await manager.InitializeAsync();

            factory.OnIsAlive = _ => throw new InvalidOperationException("alive query broken");

            var lease = await manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances")
                .WaitAsync(TestTimeout);
            lease.Dispose();

            await V2Test.WaitUntilAsync(() => manager.GetSnapshot().PoolRows[0].Active == 0);
            Assert.Equal(0, factory.DestroyCount);
            Assert.Equal(0, manager.GetSnapshot().PoolRows[0].Idle);
        }

        [Fact]
        public async Task 清池销毁闲置实例并在下次租用时重新加载原型()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out _, out var factory);
            await manager.InitializeAsync();

            var lease = await manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances")
                .WaitAsync(TestTimeout);
            var instance = ReadLease(context, lease);
            lease.Dispose();
            await V2Test.WaitUntilAsync(() => manager.GetSnapshot().PoolRows[0].Idle == 1);

            await manager.ClearPoolAsync("asset", ResourceSource.Local, "instances").WaitAsync(TestTimeout);

            await V2Test.WaitUntilAsync(() => instance.Destroyed);
            Assert.Equal(1, factory.DestroyCount);

            using var again = await manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances")
                .WaitAsync(TestTimeout);
            Assert.Equal(2, factory.CreateCount);
            Assert.NotSame(instance, ReadLease(context, again));
        }

        [Fact]
        public async Task 未注册工厂按诊断码失败()
        {
            using var context = new TestExecutionContext();
            using var manager = new ResourceManager(context);
            manager.RegisterLoader(ResourceSource.Local, new FakeLoader());
            await manager.InitializeAsync();

            var error = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "missing"));
            Assert.Equal(DiagnosticCodes.InstanceUnsupported, error.Error.DiagnosticCode);
        }

        [Fact]
        public async Task 工厂不能创建该类型时按诊断码失败且不创建实例()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out _, out var factory);
            factory.OnCanCreate = (_, _) => false;
            await manager.InitializeAsync();

            var error = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances"));
            Assert.Equal(DiagnosticCodes.InstanceUnsupported, error.Error.DiagnosticCode);
            Assert.Equal(0, factory.CreateCount);
        }

        [Fact]
        public async Task 创建失败只影响本次租用且后续请求可以重试()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out _, out var factory);
            await manager.InitializeAsync();

            factory.OnCreate = (_, _) => throw new InvalidOperationException("create broken");
            var error = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances").WaitAsync(TestTimeout));
            Assert.Equal(DiagnosticCodes.InstanceCreateFailed, error.Error.DiagnosticCode);

            factory.OnCreate = null;
            using var lease = await manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances")
                .WaitAsync(TestTimeout);
            Assert.NotNull(ReadLease(context, lease));
            Assert.Equal(2, factory.CreateCount);
        }

        [Fact]
        public async Task 闲置期满后实例被销毁且桶在超期后关闭()
        {
            using var context = new TestExecutionContext();
            var clock = new FakeClock();
            var loader = new FakeLoader();
            var factory = new FakeInstanceFactory();
            var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics(),
                monotonicNow: () => clock.Now);
            manager.RegisterLoader(ResourceSource.Local, loader);
            manager.RegisterFactory("instances", factory,
                new PoolPolicy { IdleLifetime = TimeSpan.FromSeconds(5) });
            await manager.InitializeAsync();

            var lease = await manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances")
                .WaitAsync(TestTimeout);
            var instance = ReadLease(context, lease);
            lease.Dispose();
            await V2Test.WaitUntilAsync(() => manager.GetSnapshot().PoolRows[0].Idle == 1);

            clock.Advance(TimeSpan.FromSeconds(6));
            manager.Tick();

            await V2Test.WaitUntilAsync(() => instance.Destroyed);
            await V2Test.WaitUntilAsync(() => manager.GetSnapshot().PoolRows.Count == 0);
            Assert.Equal(1, factory.DestroyCount);
        }

        [Fact]
        public async Task 租约重复归还只生效一次()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out _, out var factory);
            await manager.InitializeAsync();

            var lease = await manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, "instances")
                .WaitAsync(TestTimeout);
            lease.Dispose();
            lease.Dispose();
            lease.Dispose();

            await V2Test.WaitUntilAsync(() => manager.GetSnapshot().PoolRows[0].Active == 0);
            var row = Assert.Single(manager.GetSnapshot().PoolRows);
            Assert.Equal(1, row.Idle);
            Assert.Equal(0, factory.DestroyCount);
        }
    }
}
