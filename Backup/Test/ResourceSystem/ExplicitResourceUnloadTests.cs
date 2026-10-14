using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem;

public sealed class ExplicitResourceUnloadTests
{
    [Fact]
    public async Task SelectionReacquiresResource_UnloadRechecksCurrentHoldsBeforeReleasing()
    {
        using var manager = V2Test.NewManager(out _, out _);
        var loader = new FakeLoader();
        manager.RegisterLoader(ResourceSource.Local, loader);
        await manager.InitializeAsync();
        var first = await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
        var value = first.Value;
        first.Dispose();
        Task<ResourceRef<FakeAsset>>? acquiredInSelector = null;

        var result = await manager.UnloadUnusedAsync(row =>
        {
            Assert.Equal(0, row.HoldCount);
            acquiredInSelector = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
            return true;
        });
        using var acquired = await acquiredInSelector!;
        Assert.Same(value, acquired.Value);
        Assert.True(acquired.IsValid);
        Assert.Equal(0, loader.ReleaseCount);
        Assert.Equal(0, result.ReleasedCount);
        Assert.Equal(1, result.SkippedInUseCount);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ConverterFailure_ConcreteNativeRemaindersAreRetainedWithoutBlockingNextLoad()
    {
        using var manager = V2Test.NewManager(out _, out _);
        var texture = new object();
        var nativeBuffer = new object();
        var error = new LoadError(DiagnosticCodes.AssetLoadFailed,
            LoadStage.LoadAsset, CleanupStatus.Incomplete,
            new InvalidOperationException("conversion rollback failed"));
        var loader = new FakeLoader
        {
            LoadErrors = new Queue<Exception>(new[]
            {
                new ResourceLoadException(error, new[] { texture, nativeBuffer }),
            }),
        };
        manager.RegisterLoader(ResourceSource.Local, loader);
        await manager.InitializeAsync();
        var failure = await Assert.ThrowsAsync<ResourceLoadException>(
            () => manager.LoadAsync<FakeAsset>("image", ResourceSource.Local));
        var dirty = Assert.Single(manager.GetSnapshot().DirtyResources);
        Assert.Equal(error.DiagnosticId, failure.Error.DiagnosticId);
        Assert.Equal(error.DiagnosticId, dirty.Error.DiagnosticId);
        Assert.Equal(new[] { texture, nativeBuffer }, dirty.RetainedObjects);
        Assert.Empty(manager.GetSnapshot().ResourceRows);

        var reference = await manager.LoadAsync<FakeAsset>("image", ResourceSource.Local);
        Assert.True(reference.IsValid);
        Assert.Equal(2, loader.LoadCount);
        reference.Dispose();
        var unloaded = await manager.UnloadUnusedAsync();
        Assert.Equal(1, unloaded.ReleasedCount);
        Assert.Empty(unloaded.Failures);
        Assert.Equal(1, loader.ReleaseCount);
        Assert.Same(dirty, Assert.Single(manager.GetSnapshot().DirtyResources));
    }

    [Fact]
    public async Task ReleaseFailure_DoesNotPreventOtherSelectedProductsFromBeingReleased()
    {
        using var manager = V2Test.NewManager(out _, out _);
        var healthyReleases = 0;
        var broken = new FakeAsset("broken");
        var loader = new FakeLoader
        {
            ResultOverrides = new Queue<LoadedAsset?>(new[]
            {
                new LoadedAsset(broken, releaseAsync: () =>
                    Task.FromException(new InvalidOperationException("native release failed"))),
                new LoadedAsset(new FakeAsset("healthy"), releaseAsync: () =>
                {
                    ++healthyReleases;
                    return Task.CompletedTask;
                }),
            }),
        };
        manager.RegisterLoader(ResourceSource.Local, loader);
        await manager.InitializeAsync();
        var first = await manager.LoadAsync<FakeAsset>("broken", ResourceSource.Local);
        var second = await manager.LoadAsync<FakeAsset>("healthy", ResourceSource.Local);
        first.Dispose();
        second.Dispose();
        var unloaded = await manager.UnloadUnusedAsync();
        Assert.Equal(1, unloaded.ReleasedCount);
        Assert.Contains(broken, Assert.Single(unloaded.Failures).RetainedObjects);
        Assert.Equal(1, healthyReleases);
        Assert.Empty(manager.GetSnapshot().ResourceRows);
    }
}
