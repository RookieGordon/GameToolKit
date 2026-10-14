using ToolKit.Tools.Common;
using UnityToolKit.Runtime.Resource;

namespace ToolKit.Tests.ResourceSystem;

public sealed class LoadPreparationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ResultInvalidatedBeforePublication_FailsWithoutReloadingAndReleasesItsOwnResult()
    {
        using var pump = new MainThreadTestPump();
        var manager = new ResourceManager(new UnityExecutionContext(),
            new ResourceSystemOptions { RequestTimeout = null });
        var loader = new ControlledResultLoader();
        manager.RegisterLoader(ResourceSource.Local, loader);
        Assert.True(manager.InitializeAsync().IsCompletedSuccessfully);
        var pending = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);

        pump.RunUntil(() => loader.LoadCount == 1);
        loader.FinishLoading.SetResult(true);
        pump.WaitForPost(); // 后端完成，产物尚未在主线程验证与发布。
        loader.IsAlive = false;
        pump.RunUntil(() => loader.ReleaseCount == 1);
        Assert.False(loader.FinishReleasing.Task.IsCompleted);
        Assert.Equal(1, loader.LoadCount);
        loader.FinishReleasing.SetResult(true);
        pump.RunUntil(() => pending.IsCompleted);
        var error = Assert.Throws<ResourceLoadException>(() => pending.GetAwaiter().GetResult());
        Assert.Equal(DiagnosticCodes.LoaderInvalidResult, error.Error.DiagnosticCode);
        pump.RunUntil(() => manager.GetSnapshot().ResourceRows.Count == 0);
        Assert.Equal(1, loader.ReleaseCount);
        Assert.Equal(1, loader.LoadCount);
        manager.Dispose();
    }

    [Fact]
    public void ResultInvalidatedAfterDelivery_InvalidatesReferenceWithoutChangingSuccessfulTask()
    {
        using var pump = new MainThreadTestPump();
        var manager = new ResourceManager(new UnityExecutionContext(),
            new ResourceSystemOptions { RequestTimeout = null });
        var loader = new ControlledResultLoader();
        manager.RegisterLoader(ResourceSource.Local, loader);
        Assert.True(manager.InitializeAsync().IsCompletedSuccessfully);
        var pending = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
        loader.FinishLoading.SetResult(true);
        pump.RunUntil(() => pending.IsCompleted);
        var reference = pending.GetAwaiter().GetResult();
        Assert.True(reference.IsValid);

        loader.IsAlive = false;
        Assert.True(pending.IsCompletedSuccessfully);
        Assert.False(reference.IsValid);
        var error = Assert.Throws<ResourceLoadException>(() => reference.Value);
        Assert.Equal(DiagnosticCodes.AssetInvalidated, error.Error.DiagnosticCode);
        Assert.Equal(1, loader.LoadCount);
        reference.Dispose();
        // Unity 的凭证归还是投递通知，先让归还生效，再由业务发起卸载。
        pump.RunUntil(() => Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount == 0);
        Assert.Equal(0, loader.ReleaseCount); // 归还和失效查询本身均不触发清理。
        var unload = manager.UnloadUnusedAsync();
        pump.RunUntil(() => loader.ReleaseCount == 1);
        loader.FinishReleasing.SetResult(true);
        pump.RunUntil(() => unload.IsCompleted);
        Assert.Equal(1, unload.GetAwaiter().GetResult().ReleasedCount);
        Assert.Empty(manager.GetSnapshot().ResourceRows);
        Assert.Equal(1, loader.ReleaseCount);
        manager.Dispose();
    }

    [Fact]
    public async Task InvalidIdleResource_LoadOnlyDetachesOldProduct_ExplicitUnloadIsolatesFailure()
    {
        var manager = V2Test.NewManager(out _, out _);
        var alive = true;
        var releases = 0;
        var cause = new InvalidOperationException("native resource release failed");
        var oldValue = new FakeAsset("old");
        var loader = new FakeLoader
        {
            ResultOverrides = new Queue<LoadedAsset?>(new[]
            {
                new LoadedAsset(oldValue, () => alive, () =>
                {
                    Interlocked.Increment(ref releases);
                    return Task.FromException(cause);
                }),
            }),
        };
        manager.RegisterLoader(ResourceSource.Local, loader);
        await manager.InitializeAsync();
        var first = await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
        first.Dispose();
        alive = false;
        var next = await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local).WaitAsync(TestTimeout);
        Assert.Equal(2, loader.LoadCount);
        Assert.Equal(0, releases);
        Assert.True(next.IsValid);
        var unload = await manager.UnloadUnusedAsync();
        var dirty = Assert.Single(unload.Failures);
        Assert.Equal(1, unload.SkippedInUseCount);
        Assert.Same(cause, dirty.Error.Cause);
        Assert.Contains(oldValue, dirty.RetainedObjects);
        using var reused = await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
        Assert.Same(next.Value, reused.Value);
        Assert.Equal(1, releases);
        next.Dispose();
        manager.Dispose();
        Assert.Equal(1, releases);
    }

    [Fact]
    public void PublishedSharedResult_AlreadyOwnsEveryReferenceAndHasNoWaitingRequests()
    {
        using var pump = new MainThreadTestPump();
        var manager = new ResourceManager(new UnityExecutionContext(), new ResourceSystemOptions { RequestTimeout = null });
        var loader = new GatedLoader();
        manager.RegisterLoader(ResourceSource.Local, loader);
        Assert.True(manager.InitializeAsync().IsCompletedSuccessfully);
        var first = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
        var second = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
        Assert.Equal(2, Assert.Single(manager.GetSnapshot().ResourceRows).WaitingCount);

        loader.ReleaseGate("asset");
        while (Assert.Single(manager.GetSnapshot().ResourceRows).State == ResourceState.Loading)
            pump.RunNext();
        // 发布和建立所有持有是同一步，不再等待各调用者另行提交领取。
        var delivered = Assert.Single(manager.GetSnapshot().ResourceRows);
        Assert.Equal(ResourceState.Ready, delivered.State);
        Assert.Equal(2, delivered.HoldCount);
        Assert.Equal(0, delivered.WaitingCount);

        pump.RunUntil(() => first.IsCompleted && second.IsCompleted);
        var references = new[] { first.GetAwaiter().GetResult(), second.GetAwaiter().GetResult() };
        Assert.Same(references[0].Value, references[1].Value);
        foreach (var reference in references) reference.Dispose();
        manager.Dispose();
        pump.RunUntil(() => loader.ReleaseCount == 1);
    }

    private sealed class ControlledResultLoader : IResourceLoader
    {
        public readonly TaskCompletionSource<bool> FinishLoading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> FinishReleasing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsAlive = true;
        public int LoadCount;
        public int ReleaseCount;

        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedResource(request.Address, typeof(FakeAsset)));

        public async Task<LoadedAsset> LoadAsync(ResolvedResource resource, IProgress<ResourceProgress> progress,
            CancellationToken operationToken)
        {
            Interlocked.Increment(ref LoadCount);
            await FinishLoading.Task.ConfigureAwait(false);
            return new LoadedAsset(new FakeAsset(resource.LocalKey), () => IsAlive, () =>
            {
                Interlocked.Increment(ref ReleaseCount);
                return FinishReleasing.Task;
            });
        }
    }

}
