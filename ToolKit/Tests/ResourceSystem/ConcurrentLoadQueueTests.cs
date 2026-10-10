using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem;

public sealed class ConcurrentLoadQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PausedRequestAfterRegistration_KeepsItsFailureBatch_WhenNewRoundStarts()
    {
        var context = new DelayedCompletionContext();
        var manager = NewManager(context);
        var loader = new ControlledLoader();
        manager.RegisterLoader("fake", loader);

        var first = manager.LoadAsync<FakeAsset>("asset");
        await loader.FirstStarted.Task.WaitAsync(Timeout);
        var pause = context.DelayNextActionCompletion();
        var oldWaitingRequest = manager.LoadAsync<FakeAsset>("asset");
        await pause.ActionRan.Task.WaitAsync(Timeout);

        loader.First.TrySetException(LoadFailure());
        var firstError = await Assert.ThrowsAsync<ResourceLoadException>(() => first.WaitAsync(Timeout));
        var nextRound = manager.LoadAsync<FakeAsset>("asset");
        await loader.SecondStarted.Task.WaitAsync(Timeout);
        var nextRoundWaiting = manager.LoadAsync<FakeAsset>("asset");

        // 第一轮已失败，但旧请求的续体直到新一轮开始后才恢复。
        // 它必须立即得到第一轮错误，不能排进第二轮、等待并接收第二轮错误。
        pause.Resume.TrySetResult(true);
        var oldError = await Assert.ThrowsAsync<ResourceLoadException>(() => oldWaitingRequest.WaitAsync(Timeout));
        Assert.Equal(firstError.Error.DiagnosticId, oldError.Error.DiagnosticId);
        Assert.False(nextRound.IsCompleted);

        loader.Second.TrySetException(LoadFailure());
        var nextError = await Assert.ThrowsAsync<ResourceLoadException>(() => nextRound.WaitAsync(Timeout));
        var nextWaitingError = await Assert.ThrowsAsync<ResourceLoadException>(() => nextRoundWaiting.WaitAsync(Timeout));
        Assert.NotEqual(firstError.Error.DiagnosticId, nextError.Error.DiagnosticId);
        Assert.Equal(nextError.Error.DiagnosticId, nextWaitingError.Error.DiagnosticId);
        Assert.Equal(2, loader.LoadCount);
        await manager.ShutdownAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task PausedRequestBeforeClaiming_KeepsResultAlive_WhenFirstReferenceIsDisposed()
    {
        var context = new DelayedCompletionContext();
        var manager = NewManager(context);
        var loader = new ControlledLoader();
        manager.RegisterLoader("fake", loader);

        var first = manager.LoadAsync<FakeAsset>("asset");
        await loader.FirstStarted.Task.WaitAsync(Timeout);
        var pause = context.DelayNextActionCompletion();
        var waiting = manager.LoadAsync<FakeAsset>("asset");
        await pause.ActionRan.Task.WaitAsync(Timeout);

        var asset = new FakeAsset("asset");
        loader.First.TrySetResult(new LoadedAsset(asset, 1, null, () =>
        {
            Interlocked.Increment(ref loader.ReleaseCount);
            return Task.CompletedTask;
        }));
        var firstReference = await first.WaitAsync(Timeout);
        firstReference.Dispose();
        Assert.Equal(0, loader.ReleaseCount);

        pause.Resume.TrySetResult(true);
        var waitingReference = await waiting.WaitAsync(Timeout);
        Assert.Same(asset, waitingReference.Value);
        Assert.NotSame(firstReference, waitingReference);
        Assert.Equal(1, loader.LoadCount);
        waitingReference.Dispose();
        await manager.ShutdownAsync().WaitAsync(Timeout);
        Assert.Equal(1, loader.ReleaseCount);
    }

    private static ResourceManager NewManager(IExecutionContext context) => new ResourceManager(context,
        new ResourceSystemOptions
        {
            DefaultLoader = "fake",
            RequestTimeout = null,
            Memory = new MemoryPolicy { IdleLifetime = TimeSpan.Zero },
        });

    private static ResourceLoadException LoadFailure() => new ResourceLoadException(new LoadError(
        DiagnosticCodes.AssetLoadFailed, LoadStage.LoadAsset, CleanupStatus.Complete));

    private sealed class ControlledLoader : IResourceLoader
    {
        public readonly TaskCompletionSource<LoadedAsset> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<LoadedAsset> Second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> FirstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> SecondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LoadCount;
        public int ReleaseCount;

        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedResource(request.Address, typeof(FakeAsset)));

        public Task<LoadedAsset> LoadAsync(ResolvedResource resource, IProgress<ResourceProgress> progress,
            CancellationToken operationToken)
        {
            if (Interlocked.Increment(ref LoadCount) == 1)
            {
                FirstStarted.TrySetResult(true);
                return First.Task;
            }
            SecondStarted.TrySetResult(true);
            return Second.Task;
        }
    }

    /// <summary>操作已经提交，但其调用方的续体可单独暂停，以控制真实异步调度可能出现的先后顺序。</summary>
    private sealed class DelayedCompletionContext : IExecutionContext
    {
        private readonly ImmediateExecutionContext _inner = new();
        private PausedCompletion? _nextPause;

        public sealed class PausedCompletion
        {
            public readonly TaskCompletionSource<bool> ActionRan = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public PausedCompletion DelayNextActionCompletion()
        {
            var pause = new PausedCompletion();
            if (Interlocked.CompareExchange(ref _nextPause, pause, null) != null)
                throw new InvalidOperationException("A pause is already pending.");
            return pause;
        }

        public bool IsCurrent => true;
        public void AssertAccess() { }
        public void Post(Action action) => _inner.Post(action);
        public void Invoke(Action action) => _inner.Invoke(action);
        public T Invoke<T>(Func<T> function) => _inner.Invoke(function);
        public Task<T> RunAsync<T>(Func<T> function) => _inner.RunAsync(function);
        public Task<T> InvokeAsync<T>(Func<Task<T>> function) => _inner.InvokeAsync(function);

        public Task RunAsync(Action action)
        {
            var pause = Interlocked.Exchange(ref _nextPause, null);
            var operation = _inner.RunAsync(action);
            if (pause == null) return operation;
            pause.ActionRan.TrySetResult(true);
            return DelayCompletion(operation, pause.Resume.Task);
        }

        private static async Task DelayCompletion(Task operation, Task resume)
        {
            await operation.ConfigureAwait(false);
            await resume.ConfigureAwait(false);
        }
    }
}
