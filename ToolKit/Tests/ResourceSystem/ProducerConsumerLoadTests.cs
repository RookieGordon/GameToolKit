using System.Collections.Concurrent;
using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem;

public sealed class ProducerConsumerLoadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DifferentKeys_RespectConsumerLimit_AndFillVacanciesAsLoadsFinish()
    {
        using var manager = NewManager(out var loader, concurrency: 2);
        await manager.InitializeAsync();

        var requests = Enumerable.Range(0, 4)
            .Select(i => manager.LoadAsync<FakeAsset>("asset-" + i, source: ResourceSource.Local)).ToArray();
        await V2Test.WaitUntilAsync(() => loader.Started.Count == 2);
        Assert.Equal(2, loader.ActiveCount);
        Assert.All(requests, request => Assert.False(request.IsCompleted));

        var initial = loader.Started.ToArray();
        initial[0].Complete();
        await V2Test.WaitUntilAsync(() => loader.Started.Count == 3);
        Assert.Equal(2, loader.ActiveCount);
        Assert.Equal(2, loader.MaxActiveCount);

        initial[1].Complete();
        await V2Test.WaitUntilAsync(() => loader.Started.Count == 4);
        Assert.Equal(2, loader.ActiveCount);
        foreach (var attempt in loader.Started) attempt.Complete();

        var references = await Task.WhenAll(requests).WaitAsync(Timeout);
        Assert.Equal(4, references.Select(reference => reference.Value.Name).Distinct().Count());
        Assert.Equal(2, loader.MaxActiveCount);
        foreach (var reference in references) reference.Dispose();
        await manager.UnloadUnusedAsync();
        await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 4);
    }

    [Fact]
    public async Task QueuedKey_AllRequestsCancel_SkipsBackendWhenConsumerBecomesAvailable()
    {
        using var manager = NewManager(out var loader, concurrency: 1);
        await manager.InitializeAsync();
        var blocker = manager.LoadAsync<FakeAsset>("blocker", source: ResourceSource.Local);
        await V2Test.WaitUntilAsync(() => loader.Started.Count == 1);
        using var cancellation = new CancellationTokenSource();
        var cancelledRequests = Enumerable.Range(0, 3).Select(_ => manager.LoadAsync<FakeAsset>(
            "cancelled", source: ResourceSource.Local, cancellationToken: cancellation.Token)).ToArray();
        Assert.All(cancelledRequests, request => Assert.False(request.IsCompleted));

        cancellation.Cancel();
        foreach (var request in cancelledRequests)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Timeout));

        // 后面的真实请求得到执行机会，证明队列已经越过被取消的资源。
        var survivor = manager.LoadAsync<FakeAsset>("survivor", source: ResourceSource.Local);
        Assert.Single(loader.Started);
        loader.Started.Single().Complete();
        await V2Test.WaitUntilAsync(() => loader.Started.Any(attempt => attempt.Key == "survivor"));
        loader.Started.Single(attempt => attempt.Key == "survivor").Complete();

        var references = await Task.WhenAll(blocker, survivor).WaitAsync(Timeout);
        Assert.Equal(new[] { "blocker", "survivor" }, loader.Started.Select(attempt => attempt.Key));
        Assert.Equal(1, loader.MaxActiveCount);
        foreach (var reference in references) reference.Dispose();
        await manager.UnloadUnusedAsync();
        await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 2);
    }

    [Fact]
    public async Task QueuedSharedKey_OneCallerCancels_RemainingCallersGetIndependentReferencesFromOneLoad()
    {
        using var manager = NewManager(out var loader, concurrency: 1);
        await manager.InitializeAsync();
        var blocker = manager.LoadAsync<FakeAsset>("blocker", ResourceSource.Local);
        await V2Test.WaitUntilAsync(() => loader.Started.Count == 1);
        using var cancellation = new CancellationTokenSource();
        var cancelled = manager.LoadAsync<FakeAsset>("shared", ResourceSource.Local,
            cancellationToken: cancellation.Token);
        var first = manager.LoadAsync<FakeAsset>("shared", ResourceSource.Local);
        var second = manager.LoadAsync<FakeAsset>("shared", ResourceSource.Local);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Timeout));
        Assert.Single(loader.Started);

        loader.Started.Single().Complete();
        await V2Test.WaitUntilAsync(() => loader.Started.Count == 2);
        var sharedAttempt = Assert.Single(loader.Started, attempt => attempt.Key == "shared");
        sharedAttempt.Complete();
        var references = await Task.WhenAll(first, second).WaitAsync(Timeout);
        Assert.NotSame(references[0], references[1]);
        Assert.Same(sharedAttempt.Asset, references[0].Value);
        Assert.Same(references[0].Value, references[1].Value);
        references[0].Dispose();
        Assert.True(references[1].IsValid);
        references[1].Dispose();
        (await blocker.WaitAsync(Timeout)).Dispose();
        await manager.UnloadUnusedAsync();
        Assert.Equal(2, loader.Started.Count);
        Assert.Equal(2, loader.ReleaseCount);
    }

    [Fact]
    public async Task AbandonedBackendStillRunning_NewRequestForSameKeyWaitsWithoutOverlapping()
    {
        using var manager = NewManager(out var loader, concurrency: 2);
        await manager.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        var abandoned = manager.LoadAsync<FakeAsset>(
            "shared", source: ResourceSource.Local, cancellationToken: cancellation.Token);
        await V2Test.WaitUntilAsync(() => loader.Started.Count == 1);
        var oldAttempt = loader.Started.Single();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Timeout));
        await V2Test.WaitUntilAsync(() => oldAttempt.OperationToken.IsCancellationRequested);
        var replacement = manager.LoadAsync<FakeAsset>("shared", source: ResourceSource.Local);
        var otherKey = manager.LoadAsync<FakeAsset>("other", source: ResourceSource.Local);
        await V2Test.WaitUntilAsync(() => loader.Started.Any(attempt => attempt.Key == "other"));

        // 另一个 key 可以运行；同 key 必须等忽略取消的旧后端实际结束。
        Assert.Single(loader.Started, attempt => attempt.Key == "shared");
        Assert.False(replacement.IsCompleted);
        oldAttempt.Complete();
        await V2Test.WaitUntilAsync(() => loader.Started.Count(attempt => attempt.Key == "shared") == 2);
        var replacementAttempt = loader.Started.Last(attempt => attempt.Key == "shared");
        replacementAttempt.Complete();
        loader.Started.Single(attempt => attempt.Key == "other").Complete();

        var references = await Task.WhenAll(replacement, otherKey).WaitAsync(Timeout);
        Assert.Same(replacementAttempt.Asset, references[0].Value);
        Assert.NotSame(oldAttempt.Asset, references[0].Value);
        Assert.Equal(1, loader.MaxActiveForSameKey);
        foreach (var reference in references) reference.Dispose();
        await manager.UnloadUnusedAsync();
        await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 3);
    }

    [Fact]
    public async Task IdleConsumer_WakesForRequestsAfterItsPreviousQueueHasDrained()
    {
        using var manager = NewManager(out var loader, concurrency: 1);
        await manager.InitializeAsync();
        for (var round = 0; round < 2; ++round)
        {
            var pending = manager.LoadAsync<FakeAsset>("round-" + round, ResourceSource.Local);
            await V2Test.WaitUntilAsync(() => loader.Started.Count == round + 1);
            loader.Started.Last().Complete();
            var reference = await pending.WaitAsync(Timeout);
            Assert.Equal("round-" + round, reference.Value.Name);
            reference.Dispose();
            await manager.UnloadUnusedAsync();
            Assert.Equal(round + 1, loader.ReleaseCount);
        }
        Assert.Equal(1, loader.MaxActiveCount);
    }

    [Fact]
    public async Task SaturatedSource_DoesNotBlockAnotherSourcesConsumer()
    {
        using var manager = NewManager(out var local, concurrency: 1);
        var remote = new ControlledLoader();
        manager.RegisterLoader(ResourceSource.Remote, remote, new LoaderPolicy { MaxConcurrentLoads = 1 });
        await manager.InitializeAsync();
        var first = manager.LoadAsync<FakeAsset>("first", ResourceSource.Local);
        await V2Test.WaitUntilAsync(() => local.Started.Count == 1);
        var queued = manager.LoadAsync<FakeAsset>("queued", ResourceSource.Local);
        var independent = manager.LoadAsync<FakeAsset>("remote", ResourceSource.Remote);
        await V2Test.WaitUntilAsync(() => remote.Started.Count == 1);
        Assert.Single(local.Started);
        Assert.False(queued.IsCompleted);
        remote.Started.Single().Complete();
        var remoteReference = await independent.WaitAsync(Timeout);
        Assert.False(first.IsCompleted);
        remoteReference.Dispose();

        local.Started.Single().Complete();
        await V2Test.WaitUntilAsync(() => local.Started.Count == 2);
        local.Started.Last().Complete();
        foreach (var reference in await Task.WhenAll(first, queued).WaitAsync(Timeout)) reference.Dispose();
        await manager.UnloadUnusedAsync();
        Assert.Equal(2, local.ReleaseCount);
        Assert.Equal(1, remote.ReleaseCount);
    }

    [Fact]
    public async Task ZeroConcurrencyLimit_StartsEveryDistinctKeyWithoutWaitingForEarlierLoads()
    {
        using var manager = NewManager(out var loader, concurrency: 0);
        await manager.InitializeAsync();
        var pending = Enumerable.Range(0, 4)
            .Select(i => manager.LoadAsync<FakeAsset>("asset-" + i, ResourceSource.Local)).ToArray();
        await V2Test.WaitUntilAsync(() => loader.Started.Count == 4);
        Assert.Equal(4, loader.ActiveCount);
        Assert.All(pending, task => Assert.False(task.IsCompleted));
        foreach (var attempt in loader.Started) attempt.Complete();
        foreach (var reference in await Task.WhenAll(pending).WaitAsync(Timeout)) reference.Dispose();
        await manager.UnloadUnusedAsync();
        Assert.Equal(4, loader.ReleaseCount);
    }

    [Fact]
    public async Task DisposeWithQueuedLoads_StopsWaitingConsumerAndNeverStartsQueuedBackends()
    {
        using var manager = NewManager(out var loader, concurrency: 1);
        await manager.InitializeAsync();
        var active = manager.LoadAsync<FakeAsset>("active", ResourceSource.Local);
        await V2Test.WaitUntilAsync(() => loader.Started.Count == 1);
        var queued = manager.LoadAsync<FakeAsset>("queued", ResourceSource.Local);
        manager.Dispose();
        foreach (var pending in new[] { active, queued })
        {
            var failure = await Assert.ThrowsAsync<ResourceLoadException>(() => pending.WaitAsync(Timeout));
            Assert.Equal(DiagnosticCodes.LifecycleManagerDisposed, failure.Error.DiagnosticCode);
        }
        loader.Started.Single().Complete();
        await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1 && manager.GetSnapshot().ResourceRows.Count == 0);
        await Assert.ThrowsAsync<ResourceLoadException>(() => manager.LoadAsync<FakeAsset>("new", ResourceSource.Local));
        Assert.Single(loader.Started);
        Assert.Equal("active", loader.Started.Single().Key);
    }

    private static ResourceManager NewManager(out ControlledLoader loader, int concurrency)
    {
        var manager = V2Test.NewManager(out _, out _);
        loader = new ControlledLoader();
        manager.RegisterLoader(ResourceSource.Local, loader, new LoaderPolicy { MaxConcurrentLoads = concurrency });
        return manager;
    }

    /// <summary>后端故意忽略取消；测试独立控制每次执行结束，而不读取调度器内部状态。</summary>
    private sealed class ControlledLoader : IResourceLoader
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _activeByKey = new();
        public readonly ConcurrentQueue<Attempt> Started = new();
        public int ActiveCount;
        public int MaxActiveCount;
        public int MaxActiveForSameKey;
        public int ReleaseCount;

        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedResource(request.Address, typeof(FakeAsset)));

        public async Task<LoadedAsset> LoadAsync(ResolvedResource resource, IProgress<ResourceProgress> progress,
            CancellationToken operationToken)
        {
            var attempt = new Attempt(resource.LocalKey, operationToken);
            lock (_gate)
            {
                MaxActiveCount = Math.Max(MaxActiveCount, ++ActiveCount);
                _activeByKey.TryGetValue(resource.LocalKey, out var count);
                _activeByKey[resource.LocalKey] = ++count;
                MaxActiveForSameKey = Math.Max(MaxActiveForSameKey, count);
                Started.Enqueue(attempt);
            }
            try
            {
                await attempt.Finished.Task.ConfigureAwait(false);
                return new LoadedAsset(attempt.Asset, null, () =>
                {
                    Interlocked.Increment(ref ReleaseCount);
                    return Task.CompletedTask;
                });
            }
            finally
            {
                lock (_gate)
                {
                    --ActiveCount;
                    --_activeByKey[resource.LocalKey];
                }
            }
        }
    }

    private sealed class Attempt
    {
        public readonly string Key;
        public readonly FakeAsset Asset;
        public readonly CancellationToken OperationToken;
        public readonly TaskCompletionSource<bool> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Attempt(string key, CancellationToken operationToken)
        {
            Key = key;
            Asset = new FakeAsset(key);
            OperationToken = operationToken;
        }

        public void Complete() => Finished.TrySetResult(true);
    }
}
