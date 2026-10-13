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
        await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 2);
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
        await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 3);
    }

    private static ResourceManager NewManager(out ControlledLoader loader, int concurrency)
    {
        var manager = V2Test.NewManager(out _, out _, options =>
            options.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.Zero });
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
                return new LoadedAsset(attempt.Asset, 1, null, () =>
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
