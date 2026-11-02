#nullable disable
using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem;

public class InstancePoolTests
{
    private static readonly TimeSpan Timeout = TestClock.Timeout;

    private static Task<ToolKit.Tools.Common.ResourceSystem> CreateAsync(FakeLoader loader, FakeFactory factory, int maxIdle = 32)
    {
        return TestSystem.CreateAsync(loader, configure: o =>
        {
            o.InstanceFactory = factory;
            o.Pool = new PoolPolicy(maxIdle);
        });
    }

    private static int PrototypeHolds(ToolKit.Tools.Common.ResourceSystem system)
    {
        var entries = system.Snapshot().Entries;
        return entries.Count == 0 ? 0 : entries[0].Holds;
    }

    [Fact]
    public async Task RentCreatesFromPrototype_ReturnReusesIdle_BucketHoldsPrototypeOnce()
    {
        var loader = new FakeLoader();
        var factory = new FakeFactory();
        await using var system = await CreateAsync(loader, factory);

        var a = await system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        var b = await system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        Assert.Equal("product:p", a.Value.Prototype);
        Assert.NotSame(a.Value, b.Value);                              // 无闲置即创建，不排队
        Assert.Equal(1, loader.LoadCount);
        Assert.Equal(1, PrototypeHolds(system));                       // 桶聚合持有一份，不随租约增长

        var returned = a.Value;
        a.Dispose();
        a.Dispose();
        Assert.False(a.IsValid);
        Assert.Throws<ResourceException>(() => a.Value);
        Assert.Equal(1, returned.Returns);

        var c = await system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        Assert.Same(returned, c.Value);                                // 复用闲置
        Assert.Equal(2, factory.Created.Count);
        b.Dispose();
        c.Dispose();
        var bucket = Assert.Single(system.Snapshot().Pools);
        Assert.Equal(2, bucket.Idle);
        Assert.Equal(0, bucket.Active);
        Assert.Equal(1, PrototypeHolds(system));
        Assert.Equal(0, factory.Destroyed);
    }

    [Fact]
    public async Task MaxIdleZeroDestroysOnReturn_PrototypeStaysWhileBucketOpen()
    {
        var loader = new FakeLoader();
        var factory = new FakeFactory();
        await using var system = await CreateAsync(loader, factory, maxIdle: 0);

        var a = await system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        a.Dispose();
        Assert.Equal(1, factory.Destroyed);
        Assert.Equal(1, PrototypeHolds(system));                       // 桶未关闭：原型继续由桶持有
        var unload = await system.UnloadAsync();
        Assert.Single(unload.SkippedInUse);
    }

    [Fact]
    public async Task ClearDestroysIdle_ActiveLeasesDestroyOnReturn_PrototypeReleasedWhenAllDone()
    {
        var loader = new FakeLoader();
        var factory = new FakeFactory();
        await using var system = await CreateAsync(loader, factory);

        var a = await system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        var b = await system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        b.Dispose();

        var cleared = await system.ClearPoolAsync();
        Assert.Equal(1, cleared.Destroyed);
        Assert.Equal(1, cleared.StillActive);
        Assert.Empty(cleared.Failed);
        Assert.Equal(1, PrototypeHolds(system));                       // 仍有在用实例：原型不能丢
        Assert.Equal("product:p", a.Value.Prototype);                  // 已有租约不受清理影响

        var instance = a.Value;
        a.Dispose();                                                   // 晚归还：直接销毁
        Assert.True(instance.Destroyed);
        Assert.Equal(2, factory.Destroyed);
        Assert.Equal(0, PrototypeHolds(system));                       // 全部对象结束后归还原型
        Assert.Empty(system.Snapshot().Pools);
        var unload = await system.UnloadAsync();
        Assert.Single(unload.Released);
        Assert.Equal(1, loader.Products[0].Released);
    }

    [Fact]
    public async Task RentAfterClearStartsNewBucket_OldLeaseUnaffected()
    {
        var loader = new FakeLoader();
        var factory = new FakeFactory();
        await using var system = await CreateAsync(loader, factory);

        var a = await system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        await system.ClearPoolAsync();
        var b = await system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        Assert.Equal(1, loader.LoadCount);                             // 原型命中缓存，不重新加载
        Assert.Equal(2, system.Snapshot().Pools.Count);                // 旧桶等在用实例，新桶开放
        Assert.Equal(2, PrototypeHolds(system));

        a.Dispose();
        Assert.Equal(1, factory.Destroyed);                            // 旧桶：归还即销毁
        b.Dispose();
        Assert.Equal(1, factory.Destroyed);                            // 新桶：归还进闲置
        var bucket = Assert.Single(system.Snapshot().Pools);
        Assert.Equal(1, bucket.Idle);
        Assert.Equal(1, PrototypeHolds(system));
    }

    [Fact]
    public async Task ClearWhileWaitingForPrototype_FailsUnadmittedRent_AndReleasesItsHold()
    {
        var loader = new FakeLoader { AutoComplete = false };
        var factory = new FakeFactory();
        await using var system = await CreateAsync(loader, factory);

        var rent = system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        var pending = await loader.WaitForPendingAsync(0);
        await system.ClearPoolAsync();                                 // 尚未接纳：清池让它失败
        pending.Complete();

        var error = await Assert.ThrowsAsync<ResourceException>(() => rent.WaitAsync(Timeout));
        Assert.Equal(DiagnosticCodes.InstancePoolClosed, error.ResourceError.DiagnosticCode);
        Assert.Empty(factory.Created);
        Assert.Equal(0, PrototypeHolds(system));                       // 本人的原型持有已退回
        Assert.Empty(system.Snapshot().Pools);
    }

    [Fact]
    public async Task CreateFailure_ResetFailure_DestroyFailure_HaveDefiniteOutcomes()
    {
        var loader = new FakeLoader();
        var factory = new FakeFactory { CreateError = new InvalidOperationException("创建失败") };
        await using var system = await CreateAsync(loader, factory);

        var create = await Assert.ThrowsAsync<ResourceException>(() => system.RentAsync<FakeInstance>(ResourceSource.Local, "p"));
        Assert.Equal(DiagnosticCodes.InstanceCreateFailed, create.ResourceError.DiagnosticCode);
        var bucket = Assert.Single(system.Snapshot().Pools);
        Assert.Equal(0, bucket.Creating);
        Assert.Equal(1, PrototypeHolds(system));                       // 桶仍开放，原型仍由桶持有

        factory.CreateError = null;
        factory.ReturnError = new InvalidOperationException("重置失败");
        var a = await system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        var instance = a.Value;
        a.Dispose();
        Assert.True(instance.Destroyed);                               // 重置失败：不回池，直接销毁
        Assert.Equal(0, Assert.Single(system.Snapshot().Pools).Idle);

        factory.ReturnError = null;
        var b = await system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        var sticky = b.Value;
        factory.FailDestroy.Add(sticky);
        await system.ClearPoolAsync();
        b.Dispose();                                                   // 晚归还 + 销毁失败 → 残留
        var residue = Assert.Single(system.Snapshot().InstanceResidues);
        Assert.Same(sticky, residue.Instance);
        Assert.Equal(1, PrototypeHolds(system));                       // 残留仍依赖原型：不归还
        Assert.Single(system.Snapshot().Pools);

        factory.FailDestroy.Remove(sticky);
        var retry = await system.ClearPoolAsync();                     // 业务再次清池才重试，不自动
        Assert.Equal(1, retry.Destroyed);
        Assert.Empty(retry.Failed);
        Assert.Empty(system.Snapshot().InstanceResidues);
        Assert.Equal(0, PrototypeHolds(system));
        Assert.Empty(system.Snapshot().Pools);
    }

    [Fact]
    public async Task CancelDuringCreateDestroysTheLateInstance()
    {
        var loader = new FakeLoader();
        var factory = new FakeFactory();
        await using var system = await CreateAsync(loader, factory);
        using var cancel = new CancellationTokenSource();
        factory.OnCreate = () => cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => system.RentAsync<FakeInstance>(ResourceSource.Local, "p", token: cancel.Token));
        Assert.Single(factory.Created);
        Assert.True(factory.Created[0].Destroyed);                     // 迟到对象有确定归属：销毁
        var bucket = Assert.Single(system.Snapshot().Pools);
        Assert.Equal(0, bucket.Active);
        Assert.Equal(0, bucket.Creating);
    }

    [Fact]
    public async Task OnRentRunsForNewAndReusedInstances_FailuresDestroyTheInstance()
    {
        var loader = new FakeLoader();
        var factory = new FakeFactory();
        await using var system = await CreateAsync(loader, factory);

        var a = await system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        var instance = a.Value;
        Assert.Equal(1, instance.Rents);
        a.Dispose();
        var b = await system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        Assert.Same(instance, b.Value);
        Assert.Equal(2, instance.Rents);                                // 复用也经过准备
        b.Dispose();

        factory.RentError = new InvalidOperationException("准备失败");
        var reuse = await Assert.ThrowsAsync<ResourceException>(() => system.RentAsync<FakeInstance>(ResourceSource.Local, "p"));
        Assert.Equal(DiagnosticCodes.InstanceResetFailed, reuse.ResourceError.DiagnosticCode);
        Assert.True(instance.Destroyed);                                // 坏实例销毁，不回池
        Assert.Equal(0, Assert.Single(system.Snapshot().Pools).Idle);

        var fresh = await Assert.ThrowsAsync<ResourceException>(() => system.RentAsync<FakeInstance>(ResourceSource.Local, "p"));
        Assert.Equal(DiagnosticCodes.InstanceCreateFailed, fresh.ResourceError.DiagnosticCode);
        Assert.Equal(2, factory.Created.Count);
        Assert.True(factory.Created[1].Destroyed);                      // 刚建好就准备失败：对象不漏
        var bucket = Assert.Single(system.Snapshot().Pools);
        Assert.Equal(0, bucket.Active);
        Assert.Equal(0, bucket.Creating);
    }

    [Fact]
    public async Task RentWithoutFactoryIsRejected()
    {
        await using var system = await TestSystem.CreateAsync(new FakeLoader());
        var error = await Assert.ThrowsAsync<ResourceException>(() => system.RentAsync<FakeInstance>(ResourceSource.Local, "p"));
        Assert.Equal(DiagnosticCodes.InstanceUnsupported, error.ResourceError.DiagnosticCode);
    }

    [Fact]
    public async Task DisposeClosesPoolBeforeUnloadingPrototypes()
    {
        var loader = new FakeLoader();
        var factory = new FakeFactory();
        var system = await CreateAsync(loader, factory);
        var a = await system.RentAsync<FakeInstance>(ResourceSource.Local, "p");
        a.Dispose();

        await system.DisposeAsync();
        Assert.Equal(1, factory.Destroyed);
        Assert.Equal(1, loader.Products[0].Released);                  // 原型在池关闭后被卸载
    }
}
