using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem;

public sealed class ConsumerCancellationBoundaryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SynchronousCancellationCompletion_StartsNextConsumerOutsideStateSubmission()
    {
        var context = new ObservedExecutionContext();
        var loader = new SynchronousCancellationLoader(context);
        using var manager = NewManager(context);
        manager.RegisterLoader(ResourceSource.Local, loader, new LoaderPolicy { MaxConcurrentLoads = 1 });
        await manager.InitializeAsync();
        using var cancellation = new CancellationTokenSource();

        var first = manager.LoadAsync<FakeAsset>("first", ResourceSource.Local,
            cancellationToken: cancellation.Token);
        await loader.FirstStarted.Task.WaitAsync(Timeout);
        var second = manager.LoadAsync<FakeAsset>("second", ResourceSource.Local);
        Assert.Equal(1, loader.LoadCount);
        Assert.False(second.IsCompleted);

        // No test synchronization context may force the backend continuation to be asynchronous.
        await Task.Run(cancellation.Cancel).WaitAsync(Timeout);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Timeout));
        var reference = await second.WaitAsync(Timeout);
        Assert.Equal(2, loader.LoadCount);
        Assert.Equal(0, loader.SecondSubmissionDepth);
        Assert.Equal("second", reference.Value.Name);
        reference.Dispose();
    }

    [Fact]
    public async Task ClearingInitializingPool_StartsNextConsumerOutsidePoolStateSubmission()
    {
        var context = new ObservedExecutionContext();
        var loader = new SynchronousCancellationLoader(context);
        using var manager = NewManager(context);
        manager.RegisterLoader(ResourceSource.Local, loader, new LoaderPolicy { MaxConcurrentLoads = 1 });
        manager.RegisterFactory("factory", new FakeInstanceFactory());
        await manager.InitializeAsync();

        var renting = manager.RentAsync<FakeInstance>("first", ResourceSource.Local, factory: "factory");
        await loader.FirstStarted.Task.WaitAsync(Timeout);
        var loading = manager.LoadAsync<FakeAsset>("second", ResourceSource.Local);
        Assert.Equal(1, loader.LoadCount);
        Assert.False(loading.IsCompleted);

        // ClearPool cancels the prototype operation from within its own state submission.
        // A synchronously cancelled backend must not carry the next load into that submission.
        await Task.Run(() => manager.ClearPoolAsync("first", ResourceSource.Local, factory: "factory"))
            .WaitAsync(Timeout);

        var failure = await Assert.ThrowsAsync<ResourceLoadException>(() => renting.WaitAsync(Timeout));
        Assert.Equal(DiagnosticCodes.InstancePoolClosed, failure.Error.DiagnosticCode);
        var reference = await loading.WaitAsync(Timeout);
        Assert.Equal(2, loader.LoadCount);
        Assert.Equal(0, loader.SecondSubmissionDepth);
        Assert.Equal("second", reference.Value.Name);
        reference.Dispose();
    }

    [Fact]
    public async Task CancelledAttemptOwner_DoesNotTransferItsFailureToRemainingRequest()
    {
        using var manager = NewManager(new ImmediateExecutionContext());
        var loader = new FailedFirstAttemptLoader();
        manager.RegisterLoader(ResourceSource.Remote, loader, new LoaderPolicy
        {
            FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests,
            MaxConcurrentLoads = 1,
        });
        await manager.InitializeAsync();
        using var cancellation = new CancellationTokenSource();

        var first = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Remote,
            cancellationToken: cancellation.Token);
        await loader.FirstStarted.Task.WaitAsync(Timeout);
        var remaining = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Remote);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Timeout));

        loader.FirstResult.TrySetException(new ResourceLoadException(new LoadError(
            DiagnosticCodes.AssetLoadFailed, LoadStage.LoadAsset, CleanupStatus.Complete)));

        var reference = await remaining.WaitAsync(Timeout);
        Assert.Equal(2, loader.LoadCount);
        Assert.Equal("asset", reference.Value.Name);
        reference.Dispose();
    }

    private static ResourceManager NewManager(IExecutionContext context) => new(context,
        new ResourceSystemOptions
        {
            RequestTimeout = null,
        });

    private sealed class SynchronousCancellationLoader : IResourceLoader
    {
        private readonly ObservedExecutionContext _context;
        public readonly TaskCompletionSource<bool> FirstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LoadCount;
        public int SecondSubmissionDepth = -1;

        public SynchronousCancellationLoader(ObservedExecutionContext context) => _context = context;

        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedResource(request.Address, typeof(FakeAsset)));

        public Task<LoadedAsset> LoadAsync(ResolvedResource resource, IProgress<ResourceProgress> progress,
            CancellationToken operationToken)
        {
            if (Interlocked.Increment(ref LoadCount) == 1)
            {
                // A valid backend may complete its task synchronously from cancellation.
                // Deliberately use the default TCS continuation behavior to cover that path.
                var result = new TaskCompletionSource<LoadedAsset>();
                operationToken.Register(() => result.TrySetCanceled(operationToken));
                FirstStarted.TrySetResult(true);
                return result.Task;
            }

            SecondSubmissionDepth = _context.SubmissionDepth;
            return Task.FromResult(new LoadedAsset(new FakeAsset(resource.LocalKey), null,
                () => Task.CompletedTask));
        }
    }

    private sealed class FailedFirstAttemptLoader : IResourceLoader
    {
        public readonly TaskCompletionSource<bool> FirstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<LoadedAsset> FirstResult =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LoadCount;

        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedResource(request.Address, typeof(FakeAsset)));

        public Task<LoadedAsset> LoadAsync(ResolvedResource resource, IProgress<ResourceProgress> progress,
            CancellationToken operationToken)
        {
            if (Interlocked.Increment(ref LoadCount) == 1)
            {
                FirstStarted.TrySetResult(true);
                return FirstResult.Task;
            }

            return Task.FromResult(new LoadedAsset(new FakeAsset(resource.LocalKey), null,
                () => Task.CompletedTask));
        }
    }

    private sealed class ObservedExecutionContext : IExecutionContext
    {
        [ThreadStatic] private static int _submissionDepth;
        private readonly ImmediateExecutionContext _inner = new();
        public int SubmissionDepth => _submissionDepth;
        public bool IsCurrent => _inner.IsCurrent;
        public void AssertAccess() => _inner.AssertAccess();
        public void Post(Action action) => _inner.Post(() => InSubmission(action));
        public void Invoke(Action action) => _inner.Invoke(() => InSubmission(action));
        public T Invoke<T>(Func<T> function) => _inner.Invoke(() => InSubmission(function));
        public Task RunAsync(Action action) => _inner.RunAsync(() => InSubmission(action));
        public Task<T> RunAsync<T>(Func<T> function) => _inner.RunAsync(() => InSubmission(function));
        public Task<T> InvokeAsync<T>(Func<Task<T>> function) => _inner.InvokeAsync(function);

        private static void InSubmission(Action action)
        {
            _submissionDepth++;
            try { action(); }
            finally { _submissionDepth--; }
        }

        private static T InSubmission<T>(Func<T> function)
        {
            _submissionDepth++;
            try { return function(); }
            finally { _submissionDepth--; }
        }
    }
}
