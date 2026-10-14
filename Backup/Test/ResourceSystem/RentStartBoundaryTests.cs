using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class RentStartBoundaryTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task PrototypeLoad_StartsAfterPoolRegistrationCommit(bool completesSynchronously)
        {
            var context = new ObservedExecutionContext();
            var manager = NewManager(context, out var loader, out var factory);
            await manager.InitializeAsync();
            if (completesSynchronously) loader.Finish.TrySetResult(true);

            var pending = manager.RentAsync<FakeInstance>("enemy", ResourceSource.Local, "instances");
            await loader.Started.Task.WaitAsync(Timeout);
            Assert.Equal(0, loader.CommitDepthAtStart);
            loader.Finish.TrySetResult(true);

            var lease = await pending.WaitAsync(Timeout);
            Assert.Equal("enemy", lease.Value.Proto.Name);
            lease.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, factory.DestroyCount);
        }

        [Fact]
        public async Task FirstRenterCancelsAfterRegistration_StillStartsSharedPrototype()
        {
            var context = new ObservedExecutionContext();
            var manager = NewManager(context, out var loader, out var factory);
            await manager.InitializeAsync();
            var pause = PauseAfterPoolRegistration(context, manager);
            using var cancellation = new CancellationTokenSource();

            var first = manager.RentAsync<FakeInstance>("enemy", ResourceSource.Local, "instances",
                cancellationToken: cancellation.Token);
            await pause.Registered.Task.WaitAsync(Timeout);
            var second = manager.RentAsync<FakeInstance>("enemy", ResourceSource.Local, "instances");
            Assert.Equal(0, loader.LoadCount);
            Assert.Single(manager.GetSnapshot().PoolRows);

            cancellation.Cancel();
            pause.Resume.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Timeout));
            await loader.Started.Task.WaitAsync(Timeout);
            Assert.Equal(0, loader.CommitDepthAtStart);
            loader.Finish.TrySetResult(true);

            var lease = await second.WaitAsync(Timeout);
            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(1, factory.CreateCount);
            lease.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
        }

        [Fact]
        public async Task DisposeAfterPoolRegistration_CompletesPendingInitializationWithoutLoading()
        {
            var context = new ObservedExecutionContext();
            var manager = NewManager(context, out var loader, out var factory);
            await manager.InitializeAsync();
            var pause = PauseAfterPoolRegistration(context, manager);

            var pending = manager.RentAsync<FakeInstance>("enemy", ResourceSource.Local, "instances");
            await pause.Registered.Task.WaitAsync(Timeout);
            manager.Dispose();
            pause.Resume.TrySetResult(true);

            var error = await Assert.ThrowsAsync<ResourceLoadException>(() => pending.WaitAsync(Timeout));
            Assert.Equal(DiagnosticCodes.LifecycleManagerDisposed, error.Error.DiagnosticCode);
            await V2Test.WaitUntilAsync(() => manager.GetSnapshot().PoolRows.Count == 0);
            Assert.Equal(0, loader.LoadCount);
            Assert.Equal(0, factory.CreateCount);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
        }

        private static ResourceManager NewManager(ObservedExecutionContext context,
            out PrototypeLoader loader, out FakeInstanceFactory factory)
        {
            var manager = new ResourceManager(context, new ResourceSystemOptions
            {
                RequestTimeout = null,
            });
            loader = new PrototypeLoader(context);
            factory = new FakeInstanceFactory();
            manager.RegisterLoader(ResourceSource.Local, loader);
            manager.RegisterFactory("instances", factory);
            return manager;
        }

        private static PausedRegistration PauseAfterPoolRegistration(
            ObservedExecutionContext context, ResourceManager manager)
        {
            var pause = new PausedRegistration();
            context.Pause = pause;
            context.ShouldPause = () => manager.GetSnapshot().PoolRows.Count == 1;
            return pause;
        }

        private sealed class PrototypeLoader : IResourceLoader
        {
            private readonly ObservedExecutionContext _context;
            public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int LoadCount;
            public int ReleaseCount;
            public int CommitDepthAtStart = -1;

            public PrototypeLoader(ObservedExecutionContext context) { _context = context; }

            public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken token) =>
                Task.FromResult(new ResolvedResource(request.Address, typeof(FakePrefab)));

            public async Task<LoadedAsset> LoadAsync(ResolvedResource resource,
                IProgress<ResourceProgress> progress, CancellationToken token)
            {
                CommitDepthAtStart = _context.CommitDepth;
                Interlocked.Increment(ref LoadCount);
                Started.TrySetResult(true);
                await Finish.Task.ConfigureAwait(false);
                return new LoadedAsset(new FakePrefab(resource.LocalKey), null, () =>
                {
                    Interlocked.Increment(ref ReleaseCount);
                    return Task.CompletedTask;
                });
            }
        }

        private sealed class PausedRegistration
        {
            public readonly TaskCompletionSource<bool> Registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class ObservedExecutionContext : IExecutionContext
        {
            [ThreadStatic] private static int _commitDepth;
            private readonly ImmediateExecutionContext _inner = new();
            public PausedRegistration? Pause;
            public Func<bool>? ShouldPause;
            public int CommitDepth => _commitDepth;
            public bool IsCurrent => _inner.IsCurrent;
            public void AssertAccess() => _inner.AssertAccess();
            public void Post(Action action) => _inner.Post(() => InCommit(() => { action(); return true; }));
            public void Invoke(Action action) => _inner.Invoke(() => InCommit(() => { action(); return true; }));
            public T Invoke<T>(Func<T> function) => _inner.Invoke(() => InCommit(function));
            public Task<T> InvokeAsync<T>(Func<Task<T>> function) => _inner.InvokeAsync(function);
            public Task RunAsync(Action action) => RunAsync(() => { action(); return true; });

            public Task<T> RunAsync<T>(Func<T> function)
            {
                var task = _inner.RunAsync(() => InCommit(function));
                if (Pause == null || !task.IsCompletedSuccessfully || ShouldPause?.Invoke() != true) return task;
                var pause = Pause;
                Pause = null;
                pause.Registered.TrySetResult(true);
                return DelayCompletion(task, pause.Resume.Task);
            }

            private static T InCommit<T>(Func<T> function)
            {
                _commitDepth++;
                try { return function(); }
                finally { _commitDepth--; }
            }

            private static async Task<T> DelayCompletion<T>(Task<T> task, Task resume)
            {
                var result = await task.ConfigureAwait(false);
                await resume.ConfigureAwait(false);
                return result;
            }
        }
    }
}
