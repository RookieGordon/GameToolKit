/*
 * 资源系统 V2 单元测试 —— 实例池：租用/归还/关闭/销毁确认/故障隔离
 * (§14.3 P 系列 / §14.6 X03-X05)。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public class InstancePoolTests
    {
        private static ResourceManager NewRentedManager(
            out FakeLoader loader,
            out FakeInstanceFactory factory,
            Action<ResourceSystemOptions>? configure = null)
        {
            var manager = V2Test.NewManager(out _, out _, configure);
            loader = new FakeLoader { Factory = _ => new FakePrefab("proto") };
            factory = new FakeInstanceFactory();
            manager.RegisterLoader("fake", loader);
            manager.RegisterFactory("fakeFactory", factory);
            return manager;
        }

        private static Task<InstanceLease<FakeInstance>> RentAsync(ResourceManager manager, string address = "enemy")
        {
            return manager.RentAsync<FakeInstance>(address, loader: "fake", factory: "fakeFactory");
        }

        [Fact]
        public async Task P01_RentReturnReuse_SameInstanceNewLease()
        {
            var manager = NewRentedManager(out var loader, out var factory);

            var lease1 = await RentAsync(manager);
            var instance = lease1.Value;
            lease1.Dispose();

            var lease2 = await RentAsync(manager);
            Assert.Same(instance, lease2.Value);         // 复用同一实例
            Assert.NotSame(lease1, lease2);               // 租约是新对象
            Assert.False(lease1.IsValid);                 // 旧租约失效
            Assert.Throws<ObjectDisposedException>(() => lease1.Value);

            lease2.Dispose();
            await manager.ShutdownAsync();
            Assert.Equal(1, factory.CreateCount);
        }

        [Fact]
        public async Task P02_CloseWithOutstanding_IdleDestroyedActiveReturnedThenClosed()
        {
            var manager = NewRentedManager(out var loader, out var factory);

            var active = await RentAsync(manager);
            var idle = await RentAsync(manager);
            idle.Dispose(); // 归还 → 闲置

            var clearTask = manager.ClearPoolAsync("enemy", loader: "fake", factory: "fakeFactory");
            using (var waitCts = new CancellationTokenSource(100))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => clearTask.WaitAsync(waitCts.Token)); // 在用实例未归还，桶未关闭
            }
            Assert.Equal(1, factory.DestroyCount);          // 闲置实例立即销毁
            Assert.Equal(0, loader.ReleaseCount);           // 原型仍持有

            active.Dispose(); // 在用归还 → 销毁 → 原型持有归还 → 桶关闭
            await clearTask;
            Assert.Equal(2, factory.DestroyCount);
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount); // 内存策略决定物理卸载，排空后完成
        }

        [Fact]
        public async Task P03_OldPoolReturn_OnlyAffectsOldGeneration()
        {
            var manager = NewRentedManager(out var loader, out var factory);

            var oldLease = await RentAsync(manager);
            var clearTask = manager.ClearPoolAsync("enemy", loader: "fake", factory: "fakeFactory"); // 旧桶 Closing，在用仍占用

            var newLease = await RentAsync(manager); // 新桶建立
            var snapshotAfterRent = manager.GetSnapshot();
            Assert.Equal(2, snapshotAfterRent.PoolRows.Count); // 旧桶 Closing + 新桶 Open

            oldLease.Dispose(); // 旧租约归还原桶：销毁，不进入新桶
            await clearTask;
            Assert.Equal(1, factory.DestroyCount);
            Assert.Equal(1, manager.GetSnapshot().PoolRows.Count); // 旧桶关闭移除
            Assert.Equal(1, manager.GetSnapshot().PoolRows[0].Active); // 新桶计数不变

            newLease.Dispose();
            await manager.ShutdownAsync();
            Assert.Equal(2, factory.DestroyCount);
        }

        [Fact]
        public async Task P04_CreateFailure_NoLeaseDeliveredAndCountsRevert()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            factory.ThrowOnCreate = new ResourceLoadException(new LoadError(
                DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate, CleanupStatus.Complete));

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(DiagnosticCodes.InstanceCreateFailed, ex.Error.DiagnosticCode);

            factory.ThrowOnCreate = null;
            var lease = await RentAsync(manager); // 桶存活，可重试
            Assert.Equal(2, factory.CreateCount);
            lease.Dispose();
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount); // 关闭后原型引用归还一次
        }

        [Fact]
        public async Task P05_ResetFailure_InstanceDestroyedAndNotReused()
        {
            var manager = NewRentedManager(out var loader, out var factory);

            var lease1 = await RentAsync(manager);
            var instance1 = lease1.Value;
            lease1.Dispose(); // 闲置 1

            factory.ThrowOnRent = new InvalidOperationException("rent reset broken");
            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(DiagnosticCodes.InstanceResetFailed, ex.Error.DiagnosticCode);
            Assert.Equal(1, factory.DestroyCount); // 重置失败实例被销毁

            factory.ThrowOnRent = null;
            var lease2 = await RentAsync(manager); // 池继续工作
            Assert.NotSame(instance1, lease2.Value);
            lease2.Dispose();
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task P07_PoolCapacity_ExtraIdleInstancesDestroyed()
        {
            var manager = NewRentedManager(out var loader, out var factory,
                o => o.Pool = new PoolPolicy { MaxIdlePerResource = 2 });

            var leases = new[]
            {
                await RentAsync(manager),
                await RentAsync(manager),
                await RentAsync(manager)
            };
            foreach (var lease in leases)
            {
                lease.Dispose();
            }

            Assert.Equal(3, factory.CreateCount);
            Assert.Equal(1, factory.DestroyCount); // 3 归还 → 2 闲置 + 1 销毁

            var snapshot = manager.GetSnapshot();
            Assert.Equal(2, snapshot.PoolRows[0].Idle);
            await manager.ShutdownAsync();
            Assert.Equal(3, factory.DestroyCount); // 关闭时销毁剩余闲置
        }

        [Fact]
        public async Task P08_DestroyDelayed_PrototypeHeldUntilDestroyConfirmed()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            factory.DestroyGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var lease = await RentAsync(manager);
            lease.Dispose();

            var clearTask = manager.ClearPoolAsync("enemy", loader: "fake", factory: "fakeFactory");
            using (var waitCts = new CancellationTokenSource(100))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => clearTask.WaitAsync(waitCts.Token)); // 销毁未确认，桶未关闭
            }
            Assert.Equal(0, loader.ReleaseCount); // 确认前原型仍持有

            factory.DestroyGate.TrySetResult(true); // 确认销毁完成
            await clearTask;
            Assert.Equal(1, factory.DestroyCount);
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount); // 确认归还原型持有，物理卸载在排空时完成
        }

        [Fact]
        public async Task P09_DestroyFailure_KeepsPrototypeAndReports()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            factory.ThrowOnDestroy = new InvalidOperationException("destroy backend broken");

            var lease = await RentAsync(manager);
            lease.Dispose();

            var clearEx = await Assert.ThrowsAnyAsync<ResourceLoadException>(
                () => manager.ClearPoolAsync("enemy", loader: "fake", factory: "fakeFactory"));
            Assert.Equal(DiagnosticCodes.InstanceDestroyFailed, clearEx.Error.DiagnosticCode);
            Assert.Equal(CleanupStatus.Incomplete, clearEx.Error.Cleanup);
            Assert.Equal(0, loader.ReleaseCount); // 销毁失败 → 原型保持

            // 后续租用不悬挂：直接返回清理故障
            var rentEx = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(DiagnosticCodes.InstanceDestroyFailed, rentEx.Error.DiagnosticCode);
        }

        [Fact]
        public async Task X03_IncompleteFactoryRollback_ClosedTaskFailsAndPrototypeKept()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            factory.ThrowOnCreate = new ResourceLoadException(new LoadError(
                DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate, CleanupStatus.Incomplete));

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(CleanupStatus.Incomplete, ex.Error.Cleanup);

            var clearEx = await Assert.ThrowsAnyAsync<ResourceLoadException>(
                () => manager.ClearPoolAsync("enemy", loader: "fake", factory: "fakeFactory"));
            Assert.Equal(DiagnosticCodes.InstanceCreateFailed, clearEx.Error.DiagnosticCode);
            Assert.Equal(0, loader.ReleaseCount); // 无法销毁的残留：原型仍保持

            // 后续租用立即失败，不悬挂
            var again = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(DiagnosticCodes.InstanceCreateFailed, again.Error.DiagnosticCode);
        }

        [Fact]
        public async Task X04_ActiveLimit_SecondRentWaitsForReturn()
        {
            var manager = NewRentedManager(out var loader, out var factory,
                o => o.Pool = new PoolPolicy { MaxActivePerResource = 1 });

            var lease1 = await RentAsync(manager);
            var instance1 = lease1.Value;
            var task2 = RentAsync(manager);
            Assert.False(task2.IsCompleted); // 配额占满，第二请求等待

            lease1.Dispose();
            var lease2 = await task2; // 归还后交付，不突破配额 (归还实例复用)
            Assert.Same(instance1, lease2.Value);
            Assert.Equal(1, factory.CreateCount);
            lease2.Dispose();
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task X05_FactoryDescriptor_DifferentParametersUseDifferentPools()
        {
            var manager = NewRentedManager(out var loader, out var factory);

            var leaseA = await RentAsync(manager);
            var taskB = manager.RentAsync<FakeInstance>(
                "enemy", loader: "fake", factory: "fakeFactory",
                options: new RequestOptions { Parameters = "variantB" });

            var leaseB = await taskB;
            Assert.NotSame(leaseA.Value, leaseB.Value);
            Assert.Equal("variantB", factory.LastCreationRequest.Value.Parameters as string);
            factory.LastCreationRequest = null;

            // 再次租用 variantB 复用同池实例
            var instanceB = leaseB.Value;
            leaseB.Dispose();
            var leaseB2 = await manager.RentAsync<FakeInstance>(
                "enemy", loader: "fake", factory: "fakeFactory",
                options: new RequestOptions { Parameters = "variantB" });
            Assert.Same(instanceB, leaseB2.Value);
            Assert.Null(factory.LastCreationRequest); // 复用闲置实例，未再创建

            leaseA.Dispose();
            leaseB2.Dispose();
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task PoolIdleExpiry_EmptyPoolClosesAndReleasesPrototype()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            var lease = await RentAsync(manager);
            lease.Dispose();
            Assert.Equal(0, loader.ReleaseCount); // 空池保留原型

            await manager.ClearPoolAsync("enemy", loader: "fake", factory: "fakeFactory");
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount); // 桶关闭归还原型持有，内存策略决定物理卸载时机
        }

        [Fact]
        public async Task PrototypeLoadFailure_RentFailsWithLoadError()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader
            {
                LoadErrors = new Queue<Exception>(new[]
                {
                    new ResourceLoadException(new LoadError(
                        DiagnosticCodes.AssetNotFound, LoadStage.LoadAsset, CleanupStatus.Complete))
                })
            };
            var factory = new FakeInstanceFactory();
            manager.RegisterLoader("fake", loader);
            manager.RegisterFactory("fakeFactory", factory);

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(DiagnosticCodes.AssetNotFound, ex.Error.DiagnosticCode);
            Assert.Equal(0, factory.CreateCount);
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task NonInstantiablePrototype_RejectedByCanCreate()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader { Factory = _ => new FakeAsset("not-a-prefab") };
            var factory = new FakeInstanceFactory();
            manager.RegisterLoader("fake", loader);
            manager.RegisterFactory("fakeFactory", factory);

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(DiagnosticCodes.InstanceUnsupported, ex.Error.DiagnosticCode);
            Assert.Equal(0, factory.CreateCount);
            await manager.ShutdownAsync();
        }
    }
}
