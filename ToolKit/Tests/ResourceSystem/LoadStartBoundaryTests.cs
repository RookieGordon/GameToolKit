using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class LoadStartBoundaryTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task BackendSynchronousPrefix_RunsOutsideStateCommit()
        {
            var context = new ObservedExecutionContext();
            var manager = NewManager(context);
            var loader = new ControlledLoader(context);
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var pending = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
            await loader.Started.Task.WaitAsync(TestTimeout);
            Assert.Equal(0, loader.CommitDepthAtStart);

            loader.CompleteLoad.TrySetResult(true);
            var reference = await pending.WaitAsync(TestTimeout);
            reference.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task CallerCancelsAfterStartRegistration_OperationStillServesOtherWaiter()
        {
            var context = new ObservedExecutionContext();
            var manager = NewManager(context);
            var loader = new ControlledLoader(context);
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            using var cancellation = new CancellationTokenSource();
            var pause = PauseAfterStartRegistration(context, manager, loader);

            var first = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local,
                cancellationToken: cancellation.Token);
            await pause.Committed.Task.WaitAsync(TestTimeout);
            Assert.Equal(0, loader.LoadCount);
            var second = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
            Assert.Equal(2, Assert.Single(manager.GetSnapshot().ResourceRows).WaitingCount);

            cancellation.Cancel();
            pause.Resume.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TestTimeout));
            await loader.Started.Task.WaitAsync(TestTimeout);
            Assert.Equal(0, loader.CommitDepthAtStart);
            Assert.False(second.IsCompleted);

            loader.CompleteLoad.TrySetResult(true);
            var reference = await second.WaitAsync(TestTimeout);
            Assert.Equal("asset", reference.Value.Name);
            Assert.Equal(1, loader.LoadCount);
            reference.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
            Assert.True(manager.GetSnapshot().IsDisposed);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public async Task DisposeAfterStartRegistration_CleansUpWhetherBackendStarts(int maxConcurrentLoads)
        {
            var context = new ObservedExecutionContext();
            var manager = NewManager(context);
            var loader = new ControlledLoader(context);
            manager.RegisterLoader(ResourceSource.Local, loader,
                new LoaderPolicy { MaxConcurrentLoads = maxConcurrentLoads });
            await manager.InitializeAsync();
            var pause = PauseAfterStartRegistration(context, manager, loader);

            var pending = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
            await pause.Committed.Task.WaitAsync(TestTimeout);
            Assert.Equal(0, loader.LoadCount);
            manager.Dispose();
            Assert.True(manager.GetSnapshot().IsDisposed);

            pause.Resume.TrySetResult(true);
            var error = await Assert.ThrowsAsync<ResourceLoadException>(() => pending.WaitAsync(TestTimeout));
            Assert.Equal(DiagnosticCodes.LifecycleManagerDisposed, error.Error.DiagnosticCode);
            if (maxConcurrentLoads == 0)
            {
                // 没有槽位等待，忽略取消的后端继续执行，迟到结果仍由本轮操作清理。
                await loader.Started.Task.WaitAsync(TestTimeout);
                Assert.Equal(0, loader.CommitDepthAtStart);
                Assert.Equal(0, loader.ReleaseCount);
                loader.CompleteLoad.TrySetResult(true);
                await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            }

            await V2Test.WaitUntilAsync(() => manager.GetSnapshot().ResourceRows.Count == 0);
            // 有槽位限制时，已取消的操作在等待槽位处退出，无需调用后端。
            Assert.Equal(maxConcurrentLoads == 0 ? 1 : 0, loader.LoadCount);
            Assert.Equal(maxConcurrentLoads == 0 ? 1 : 0, loader.ReleaseCount);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
            Assert.Empty(manager.GetSnapshot().CleanupErrors);
            Assert.True(manager.GetSnapshot().IsDisposed);
        }

        private static ResourceManager NewManager(IExecutionContext context) => new(context,
            new ResourceSystemOptions
            {
                RequestTimeout = null,
                Memory = new MemoryPolicy { IdleLifetime = TimeSpan.Zero },
            });

        private static PausedCompletion PauseAfterStartRegistration(
            ObservedExecutionContext context, ResourceManager manager, ControlledLoader loader)
        {
            var pause = new PausedCompletion();
            context.AfterCommit = () =>
            {
                var rows = manager.GetSnapshot().ResourceRows;
                if (rows.Count != 1 || rows[0].State != ResourceState.Loading
                    || rows[0].WaitingCount != 1 || loader.LoadCount != 0)
                    return;

                // 第一个请求已经登记。下一次状态提交只登记启动；暂停其调用方续体。
                context.AfterCommit = null;
                context.PauseNextCompletion(pause);
            };
            return pause;
        }

        private sealed class ControlledLoader : IResourceLoader
        {
            private readonly ObservedExecutionContext _context;
            public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> CompleteLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int LoadCount;
            public int ReleaseCount;
            public int CommitDepthAtStart = -1;

            public ControlledLoader(ObservedExecutionContext context) { _context = context; }

            public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new ResolvedResource(request.Address, typeof(FakeAsset)));

            public async Task<LoadedAsset> LoadAsync(ResolvedResource resource,
                IProgress<ResourceProgress> progress, CancellationToken operationToken)
            {
                // 同一线程仍可能运行后端；只观察当前是否处于状态委托调用栈内。
                CommitDepthAtStart = _context.CommitDepth;
                Interlocked.Increment(ref LoadCount);
                Started.TrySetResult(true);
                await CompleteLoad.Task.ConfigureAwait(false); // 有意忽略取消，验证迟到结果收尾。
                return new LoadedAsset(new FakeAsset(resource.LocalKey), 1, null, ReleaseAsync);
            }

            private Task ReleaseAsync()
            {
                Interlocked.Increment(ref ReleaseCount);
                return Task.CompletedTask;
            }
        }

        private sealed class PausedCompletion
        {
            public readonly TaskCompletionSource<bool> Committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>记录短状态委托的调用深度，可让已提交操作的调用方稍后恢复。</summary>
        private sealed class ObservedExecutionContext : IExecutionContext
        {
            [ThreadStatic] private static int _commitDepth;
            private readonly ImmediateExecutionContext _inner = new();
            private PausedCompletion? _nextPause;
            public Action? AfterCommit;
            public int CommitDepth => _commitDepth;
            public bool IsCurrent => _inner.IsCurrent;

            public void AssertAccess() => _inner.AssertAccess();
            public void Post(Action action) => _inner.Post(() => InCommit(action));
            public void Invoke(Action action) => _inner.Invoke(() => InCommit(action));
            public T Invoke<T>(Func<T> function) => _inner.Invoke(() => InCommit(function));
            public Task<T> InvokeAsync<T>(Func<Task<T>> function) => _inner.InvokeAsync(function);

            public void PauseNextCompletion(PausedCompletion pause)
            {
                Assert.Null(Interlocked.CompareExchange(ref _nextPause, pause, null));
            }

            public Task RunAsync(Action action)
            {
                var pause = Interlocked.Exchange(ref _nextPause, null);
                var operation = _inner.RunAsync(() => InCommit(action));
                if (operation.IsCompletedSuccessfully) AfterCommit?.Invoke();
                if (pause == null) return operation;
                pause.Committed.TrySetResult(true);
                return DelayCompletion(operation, pause.Resume.Task);
            }

            public Task<T> RunAsync<T>(Func<T> function)
            {
                var pause = Interlocked.Exchange(ref _nextPause, null);
                var operation = _inner.RunAsync(() => InCommit(function));
                if (operation.IsCompletedSuccessfully) AfterCommit?.Invoke();
                if (pause == null) return operation;
                pause.Committed.TrySetResult(true);
                return DelayCompletion(operation, pause.Resume.Task);
            }

            private static void InCommit(Action action)
            {
                _commitDepth++;
                try { action(); }
                finally { _commitDepth--; }
            }

            private static T InCommit<T>(Func<T> function)
            {
                _commitDepth++;
                try { return function(); }
                finally { _commitDepth--; }
            }

            private static async Task DelayCompletion(Task operation, Task resume)
            {
                await operation.ConfigureAwait(false);
                await resume.ConfigureAwait(false);
            }

            private static async Task<T> DelayCompletion<T>(Task<T> operation, Task resume)
            {
                var result = await operation.ConfigureAwait(false);
                await resume.ConfigureAwait(false);
                return result;
            }
        }
    }
}
