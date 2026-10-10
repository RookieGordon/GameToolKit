using ToolKit.Tools.Common;
using UnityToolKit.Runtime.Resource;

namespace ToolKit.Tests.ResourceSystem;

// Compiles the real UnityExecutionContext, which has no UnityEngine dependency.
// The pump deliberately does not run until the worker has returned its task.
public sealed class UnityExecutionContextTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImmediateRunAsync_ReportsCancellationForActionAndResult(bool hasCancelledToken)
    {
        var context = new ImmediateExecutionContext();
        var token = hasCancelledToken ? new CancellationToken(canceled: true) : default;
        var expected = new OperationCanceledException(token);

        var action = context.RunAsync((Action)(() => throw expected));
        var result = context.RunAsync<int>(() => throw expected);

        Assert.True(action.IsCanceled);
        Assert.True(result.IsCanceled);
        foreach (var task in new Task[] { action, result })
        {
            var error = Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
            Assert.True(error.CancellationToken.IsCancellationRequested);
            if (hasCancelledToken) Assert.Equal(token, error.CancellationToken);
        }
        Assert.Same(expected, Record.Exception(() => context.Invoke((Action)(() => throw expected))));
    }

    [Fact]
    public void RunAsync_ReturnsToWorkerBeforeMainThreadRunsAction()
    {
        using var pump = new MainThreadTestPump();
        var context = new UnityExecutionContext();
        var executedOn = 0;

        var pending = OnWorker(() => context.RunAsync(() =>
        {
            executedOn = Environment.CurrentManagedThreadId;
            return 42;
        }));

        Assert.False(pending.IsCompleted);
        Assert.Equal(0, executedOn);
        pump.Drain();
        Assert.Equal(42, pending.GetAwaiter().GetResult());
        Assert.Equal(Environment.CurrentManagedThreadId, executedOn);
    }

    [Fact]
    public void Invoke_FromWorkerFailsWithoutPostingOrWaiting()
    {
        using var pump = new MainThreadTestPump();
        var context = new UnityExecutionContext();
        var invoked = false;

        var error = OnWorker(() => Record.Exception(() => context.Invoke(() => invoked = true)));

        Assert.IsType<InvalidOperationException>(error);
        Assert.False(invoked);
        Assert.Equal(0, pump.PendingCount);
    }

    [Fact]
    public void RunAsync_PropagatesFaultAndCancellationThroughTask()
    {
        using var pump = new MainThreadTestPump();
        var context = new UnityExecutionContext();
        var expected = new InvalidOperationException("action failed");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var faulted = OnWorker(() => context.RunAsync<int>(() => throw expected));
        var cancelled = OnWorker(() => context.RunAsync(cancellation.Token.ThrowIfCancellationRequested));
        pump.Drain();

        Assert.Same(expected, Record.Exception(() => faulted.GetAwaiter().GetResult()));
        Assert.True(cancelled.IsCanceled);
        var error = Assert.ThrowsAny<OperationCanceledException>(() => cancelled.GetAwaiter().GetResult());
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public void InvokeAsync_StartsOnMainThreadAndWaitsForActualOperation()
    {
        using var pump = new MainThreadTestPump();
        var context = new UnityExecutionContext();
        var operation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedOn = 0;
        var pending = OnWorker(() => context.InvokeAsync(() =>
        {
            startedOn = Environment.CurrentManagedThreadId;
            return operation.Task;
        }));

        pump.Drain();
        Assert.Equal(Environment.CurrentManagedThreadId, startedOn);
        Assert.False(pending.IsCompleted);
        operation.SetResult(7);
        Assert.True(pending.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(7, pending.GetAwaiter().GetResult());
    }

    [Fact]
    public void Dispose_FromWorkerReturnsImmediatelyAndRejectsNewRequestsBeforeCleanupRuns()
    {
        using var pump = new MainThreadTestPump();
        var context = new UnityExecutionContext();
        var manager = new ResourceManager(context, new ResourceSystemOptions { DefaultLoader = "fake" });
        var loader = new FakeLoader();
        manager.RegisterLoader("fake", loader);

        OnWorker(() => { manager.Dispose(); return true; });
        Assert.Equal(1, pump.PendingCount);
        var request = manager.LoadAsync<FakeAsset>("asset");
        var error = Assert.Throws<ResourceLoadException>(() => request.GetAwaiter().GetResult());
        Assert.Equal(DiagnosticCodes.LifecycleManagerClosing, error.Error.DiagnosticCode);
        Assert.Equal(0, loader.LoadCount);

        pump.Drain();
        Assert.True(manager.ShutdownAsync().IsCompletedSuccessfully);
    }

    private static T OnWorker<T>(Func<T> action)
    {
        var worker = Task.Factory.StartNew(action, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "The worker blocked before returning its task.");
        return worker.GetAwaiter().GetResult();
    }

    [Fact]
    public void BackgroundResolve_ReturnsWithoutWaitingForMainThreadCommit()
    {
        using var pump = new MainThreadTestPump();
        var manager = new ResourceManager(new UnityExecutionContext(),
            new ResourceSystemOptions { DefaultLoader = "fake", RequestTimeout = null });
        var loader = new DeferredResolveLoader();
        manager.RegisterLoader("fake", loader);
        var pending = manager.LoadAsync<FakeAsset>("asset");

        // Inline continuations deliberately reproduce the worker returning from ResolveAsync.
        OnWorker(() => { loader.Resolution.SetResult(new ResolvedResource("asset", typeof(object), "asset")); return true; });
        pump.WaitForPost();
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, loader.Inner.LoadCount);

        pump.RunUntil(() => pending.IsCompleted);
        var reference = pending.GetAwaiter().GetResult();
        Assert.Equal(1, loader.Inner.LoadCount);
        Assert.Equal("asset", reference.Value.Name);
        reference.Dispose();
        var shutdown = manager.ShutdownAsync();
        pump.RunUntil(() => shutdown.IsCompleted);
        shutdown.GetAwaiter().GetResult();
        Assert.Equal(1, loader.Inner.ReleaseCount);
    }

    private sealed class DeferredResolveLoader : IResourceLoader
    {
        public readonly TaskCompletionSource<ResolvedResource> Resolution = new();
        public readonly FakeLoader Inner = new();
        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken) => Resolution.Task;
        public Task<LoadedAsset> LoadAsync(ResolvedResource resource, IProgress<ResourceProgress> progress,
            CancellationToken operationToken) => Inner.LoadAsync(resource, progress, operationToken);
    }

    [Fact]
    public void Binder_BackgroundLoadCompletionAppliesResourceOnMainThread()
    {
        using var pump = new MainThreadTestPump();
        var context = new UnityExecutionContext();
        var manager = new ResourceManager(context,
            new ResourceSystemOptions { DefaultLoader = "fake", RequestTimeout = null });
        var loader = new GatedLoader();
        manager.RegisterLoader("fake", loader);
        var binder = new ResourceBinder(manager, context);
        var target = new BindingTarget();
        var applicator = new ThreadCheckingApplicator(context);
        var pending = binder.ApplyAsync(target, "asset", "asset", applicator);

        OnWorker(() => { loader.ReleaseGate("asset"); return true; });
        pump.RunUntil(() => pending.IsCompleted);

        Assert.Equal(BindingResult.Applied, pending.GetAwaiter().GetResult());
        Assert.Equal("asset", target.Asset!.Name);
        binder.Revert(target, "asset", applicator);
        Assert.Null(target.Asset);
        var shutdown = manager.ShutdownAsync();
        pump.RunUntil(() => shutdown.IsCompleted);
        shutdown.GetAwaiter().GetResult();
        Assert.Equal(1, loader.ReleaseCount);
    }

    private sealed class BindingTarget
    {
        public FakeAsset? Asset;
    }

    private sealed class ThreadCheckingApplicator : IResourceApplicator<BindingTarget, FakeAsset>
    {
        private readonly IExecutionContext _context;
        public ThreadCheckingApplicator(IExecutionContext context) => _context = context;
        public void Replace(BindingTarget target, FakeAsset value)
        {
            _context.AssertAccess();
            target.Asset = value;
        }
        public void Revert(BindingTarget target)
        {
            _context.AssertAccess();
            target.Asset = null;
        }
    }
}
