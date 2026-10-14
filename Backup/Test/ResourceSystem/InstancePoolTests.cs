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
            manager.RegisterLoader(ResourceSource.Local, loader);
            manager.RegisterFactory("fakeFactory", factory);
            return manager;
        }

        private static Task<InstanceLease<FakeInstance>> RentAsync(ResourceManager manager, string address = "enemy")
        {
            return manager.RentAsync<FakeInstance>(address, source: ResourceSource.Local, factory: "fakeFactory");
        }

        [Fact]
        public async Task P01_RentReturnReuse_SameInstanceNewLease()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            await manager.InitializeAsync();

            var lease1 = await RentAsync(manager);
            var instance = lease1.Value;
            lease1.Dispose();

            var lease2 = await RentAsync(manager);
            Assert.Same(instance, lease2.Value);         // 复用同一实例
            Assert.NotSame(lease1, lease2);               // 租约是新对象
            Assert.False(lease1.IsValid);                 // 旧租约失效
            Assert.Throws<ObjectDisposedException>(() => lease1.Value);

            lease2.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => factory.DestroyCount == 1);
            Assert.Equal(1, factory.CreateCount);
        }

        [Fact]
        public async Task P02_CloseWithOutstanding_IdleDestroyedActiveReturnedThenClosed()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            await manager.InitializeAsync();

            var active = await RentAsync(manager);
            var idle = await RentAsync(manager);
            idle.Dispose(); // 归还 → 闲置

            var clearTask = manager.ClearPoolAsync("enemy", source: ResourceSource.Local, factory: "fakeFactory");
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
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount); // 进程退出时释放原型
        }

        [Fact]
        public async Task P03_OldPoolReturn_OnlyAffectsOldGeneration()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            await manager.InitializeAsync();

            var oldLease = await RentAsync(manager);
            var clearTask = manager.ClearPoolAsync("enemy", source: ResourceSource.Local, factory: "fakeFactory"); // 旧桶 Closing，在用仍占用

            var newLease = await RentAsync(manager); // 新桶建立
            var snapshotAfterRent = manager.GetSnapshot();
            Assert.Equal(2, snapshotAfterRent.PoolRows.Count); // 旧桶 Closing + 新桶 Open

            oldLease.Dispose(); // 旧租约归还原桶：销毁，不进入新桶
            await clearTask;
            Assert.Equal(1, factory.DestroyCount);
            Assert.Equal(1, manager.GetSnapshot().PoolRows.Count); // 旧桶关闭移除
            Assert.Equal(1, manager.GetSnapshot().PoolRows[0].Active); // 新桶计数不变

            newLease.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => factory.DestroyCount == 2);
            Assert.Equal(2, factory.DestroyCount);
        }

        [Fact]
        public async Task P04_CreateFailure_NoLeaseDeliveredAndCountsRevert()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            await manager.InitializeAsync();
            factory.ThrowOnCreate = new ResourceLoadException(new LoadError(
                DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate, CleanupStatus.Complete));

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(DiagnosticCodes.InstanceCreateFailed, ex.Error.DiagnosticCode);

            factory.ThrowOnCreate = null;
            var lease = await RentAsync(manager); // 桶存活，可重试
            Assert.Equal(2, factory.CreateCount);
            lease.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount); // 关闭后原型引用归还一次
        }

        [Fact]
        public async Task P05_ResetFailure_InstanceDestroyedAndNotReused()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            await manager.InitializeAsync();

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
            manager.Dispose();
        }

        [Fact]
        public async Task P07_PoolCapacity_ExtraIdleInstancesDestroyed()
        {
            var manager = NewRentedManager(out var loader, out var factory,
                o => o.Pool = new PoolPolicy { MaxIdlePerResource = 2 });
            await manager.InitializeAsync();

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
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => factory.DestroyCount == 3);
            Assert.Equal(3, factory.DestroyCount); // 关闭时销毁剩余闲置
        }

        [Fact]
        public async Task P08_DestroyDelayed_PrototypeHeldUntilDestroyConfirmed()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            await manager.InitializeAsync();
            factory.DestroyGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var lease = await RentAsync(manager);
            lease.Dispose();

            var clearTask = manager.ClearPoolAsync("enemy", source: ResourceSource.Local, factory: "fakeFactory");
            using (var waitCts = new CancellationTokenSource(100))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => clearTask.WaitAsync(waitCts.Token)); // 销毁未确认，桶未关闭
            }
            Assert.Equal(0, loader.ReleaseCount); // 确认前原型仍持有

            factory.DestroyGate.TrySetResult(true); // 确认销毁完成
            await clearTask;
            Assert.Equal(1, factory.DestroyCount);
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount); // 原型归还后，进程退出执行物理卸载
        }

        [Fact]
        public async Task P09_DestroyFailure_IsolatesInstanceAndKeepsPrototypeWithoutBlockingRent()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            await manager.InitializeAsync();
            factory.ThrowOnDestroy = new InvalidOperationException("destroy backend broken");

            var lease = await RentAsync(manager);
            var failedInstance = lease.Value;
            lease.Dispose();

            var clearEx = await Assert.ThrowsAnyAsync<ResourceLoadException>(
                () => manager.ClearPoolAsync("enemy", source: ResourceSource.Local, factory: "fakeFactory"));
            Assert.Equal(DiagnosticCodes.InstanceDestroyFailed, clearEx.Error.DiagnosticCode);
            Assert.Equal(CleanupStatus.Incomplete, clearEx.Error.Cleanup);
            var dirty = Assert.Single(manager.GetSnapshot().DirtyResources);
            Assert.Contains(failedInstance, dirty.RetainedObjects);
            Assert.Empty(manager.GetSnapshot().PoolRows);

            factory.ThrowOnDestroy = null;
            var next = await RentAsync(manager);
            Assert.NotSame(failedInstance, next.Value);
            next.Dispose();
            await manager.ClearPoolAsync("enemy", source: ResourceSource.Local, factory: "fakeFactory");
            var unload = await manager.UnloadUnusedAsync();
            Assert.Equal(1, unload.SkippedInUseCount); // 脏实例独立保留原型；正常池已关闭。
            Assert.Equal(0, loader.ReleaseCount);
            Assert.Single(manager.GetSnapshot().DirtyResources);
            Assert.Equal(1, factory.DestroyCount); // 只销毁新实例，不重试旧残留。
            manager.Dispose();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DirtyInstance_RetainsPrototypeWithoutCallingBackendIsAlive(bool throwOnSecondCheck)
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new CleanupAliveLoader(throwOnSecondCheck);
            var factory = new FakeInstanceFactory();
            manager.RegisterLoader(ResourceSource.Local, loader);
            manager.RegisterFactory("fakeFactory", factory);
            await manager.InitializeAsync();
            var lease = await RentAsync(manager);
            var instance = lease.Value;
            lease.Dispose();
            loader.CleanupStarted = true;
            factory.ThrowOnDestroy = new InvalidOperationException("destroy failed");

            var clear = manager.ClearPoolAsync("enemy", ResourceSource.Local, "fakeFactory");
            var error = await Assert.ThrowsAsync<ResourceLoadException>(
                () => clear.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(DiagnosticCodes.InstanceDestroyFailed, error.Error.DiagnosticCode);
            Assert.Equal(0, loader.CleanupAliveChecks);
            var dirty = Assert.Single(manager.GetSnapshot().DirtyResources);
            Assert.Contains(instance, dirty.RetainedObjects);
            Assert.Contains(dirty.RetainedObjects, item => item is ResourceRef<object>);
            Assert.Empty(manager.GetSnapshot().PoolRows);
            var unload = await manager.UnloadUnusedAsync();
            Assert.Equal(1, unload.SkippedInUseCount); // 脏实例仍拥有原型，不因存活查询失败丢失依赖。
            manager.Dispose();
        }

        private sealed class CleanupAliveLoader : IResourceLoader
        {
            private readonly bool _throwOnSecondCheck;
            public bool CleanupStarted;
            public int CleanupAliveChecks;

            public CleanupAliveLoader(bool throwOnSecondCheck) { _throwOnSecondCheck = throwOnSecondCheck; }

            public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
                => Task.FromResult(new ResolvedResource(request.Address, typeof(FakePrefab)));

            public Task<LoadedAsset> LoadAsync(ResolvedResource resource,
                IProgress<ResourceProgress> progress, CancellationToken operationToken)
                => Task.FromResult(new LoadedAsset(new FakePrefab(resource.LocalKey), () =>
                {
                    if (!CleanupStarted) return true;
                    if (Interlocked.Increment(ref CleanupAliveChecks) == 1) return true;
                    if (_throwOnSecondCheck) throw new InvalidOperationException("alive query failed");
                    return false;
                }));
        }

        [Fact]
        public async Task X03_UnknownFactoryFailure_WithoutObjectsDoesNotInventDirtyResource()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            await manager.InitializeAsync();
            factory.ThrowOnCreate = new ResourceLoadException(new LoadError(
                DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate, CleanupStatus.Unknown));

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(CleanupStatus.Unknown, ex.Error.Cleanup);
            Assert.Empty(manager.GetSnapshot().DirtyResources);
            await manager.ClearPoolAsync("enemy", source: ResourceSource.Local, factory: "fakeFactory");
            var unload = await manager.UnloadUnusedAsync();
            Assert.Equal(1, unload.ReleasedCount);
            Assert.Equal(1, loader.ReleaseCount);

            factory.ThrowOnCreate = null;
            var next = await RentAsync(manager);
            Assert.True(next.IsValid);
            next.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task X04_ActiveLimit_SecondRentWaitsForReturn()
        {
            var manager = NewRentedManager(out var loader, out var factory,
                o => o.Pool = new PoolPolicy { MaxActivePerResource = 1 });
            await manager.InitializeAsync();

            var lease1 = await RentAsync(manager);
            var instance1 = lease1.Value;
            var task2 = RentAsync(manager);
            Assert.False(task2.IsCompleted); // 配额占满，第二请求等待

            lease1.Dispose();
            var lease2 = await task2; // 归还后交付，不突破配额 (归还实例复用)
            Assert.Same(instance1, lease2.Value);
            Assert.Equal(1, factory.CreateCount);
            lease2.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task X05_FactoryDescriptor_DifferentParametersUseDifferentPools()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            await manager.InitializeAsync();

            var leaseA = await RentAsync(manager);
            var taskB = manager.RentAsync<FakeInstance>(
                "enemy", source: ResourceSource.Local, factory: "fakeFactory",
                options: new RequestOptions { Parameters = "variantB" });

            var leaseB = await taskB;
            Assert.NotSame(leaseA.Value, leaseB.Value);
            Assert.Equal("variantB", factory.LastCreationRequest.Value.Parameters as string);
            factory.LastCreationRequest = null;

            // 再次租用 variantB 复用同池实例
            var instanceB = leaseB.Value;
            leaseB.Dispose();
            var leaseB2 = await manager.RentAsync<FakeInstance>(
                "enemy", source: ResourceSource.Local, factory: "fakeFactory",
                options: new RequestOptions { Parameters = "variantB" });
            Assert.Same(instanceB, leaseB2.Value);
            Assert.Null(factory.LastCreationRequest); // 复用闲置实例，未再创建

            leaseA.Dispose();
            leaseB2.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task PoolIdleExpiry_EmptyPoolClosesAndReleasesPrototype()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            await manager.InitializeAsync();
            var lease = await RentAsync(manager);
            lease.Dispose();
            Assert.Equal(0, loader.ReleaseCount); // 空池保留原型

            await manager.ClearPoolAsync("enemy", source: ResourceSource.Local, factory: "fakeFactory");
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount); // 桶关闭归还原型持有，进程退出执行物理卸载
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
            manager.RegisterLoader(ResourceSource.Local, loader);
            manager.RegisterFactory("fakeFactory", factory);
            await manager.InitializeAsync();

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(DiagnosticCodes.AssetNotFound, ex.Error.DiagnosticCode);
            Assert.Equal(0, factory.CreateCount);
            manager.Dispose();
        }

        [Fact]
        public async Task NonInstantiablePrototype_RejectedByCanCreate()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader { Factory = _ => new FakeAsset("not-a-prefab") };
            var factory = new FakeInstanceFactory();
            manager.RegisterLoader(ResourceSource.Local, loader);
            manager.RegisterFactory("fakeFactory", factory);
            await manager.InitializeAsync();

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(DiagnosticCodes.InstanceUnsupported, ex.Error.DiagnosticCode);
            Assert.Equal(0, factory.CreateCount);
            manager.Dispose();
        }
    }
}
