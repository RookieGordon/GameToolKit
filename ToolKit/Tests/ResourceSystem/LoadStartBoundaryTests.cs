using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityToolKit.Runtime.Resource;
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
        public async Task ConsumerRunsWhileProducerSubmissionReturnIsPaused_AndServesRemainingWaiter()
        {
            var context = new ObservedExecutionContext();
            var manager = NewManager(context);
            var loader = new ControlledLoader(context);
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            using var cancellation = new CancellationTokenSource();
            var pause = PauseAfterRequestRegistration(context, manager);

            var first = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local,
                cancellationToken: cancellation.Token);
            await pause.Committed.Task.WaitAsync(TestTimeout);
            // 生产者只负责入队；即使它尚未收到登记完成，独立消费者也能开始工作。
            await loader.Started.Task.WaitAsync(TestTimeout);
            Assert.False(pause.Resume.Task.IsCompleted);
            Assert.False(first.IsCompleted);
            Assert.Equal(0, loader.CommitDepthAtStart);
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
        public void DisposeBeforeConsumerCanCommitStart_SkipsBackendWithOrWithoutConcurrencyLimit(int maxConcurrentLoads)
        {
            using var pump = new MainThreadTestPump();
            using var manager = NewManager(new UnityExecutionContext());
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader,
                new LoaderPolicy { MaxConcurrentLoads = maxConcurrentLoads });
            Assert.True(manager.InitializeAsync().IsCompletedSuccessfully);

            var pending = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
            // 请求已经入队，尚未处理任何消费阶段的主线程提交。
            Assert.Equal(1, Assert.Single(manager.GetSnapshot().ResourceRows).WaitingCount);
            Assert.Equal(0, loader.LoadCount);
            manager.Dispose();
            Assert.True(manager.GetSnapshot().IsDisposed);

            pump.RunUntil(() => pending.IsCompleted && manager.GetSnapshot().ResourceRows.Count == 0);
            var error = Assert.Throws<ResourceLoadException>(() => pending.GetAwaiter().GetResult());
            Assert.Equal(DiagnosticCodes.LifecycleManagerDisposed, error.Error.DiagnosticCode);
            pump.Drain();
            Assert.Equal(0, loader.LoadCount);
            Assert.Equal(0, loader.ReleaseCount);
            Assert.Empty(manager.GetSnapshot().DirtyResources);
        }

        private static ResourceManager NewManager(IExecutionContext context) => new(context,
            new ResourceSystemOptions
            {
                RequestTimeout = null,
            });

        private static PausedCompletion PauseAfterRequestRegistration(
            ObservedExecutionContext context, ResourceManager manager)
        {
            var pause = new PausedCompletion();
            var producerThread = Thread.CurrentThread.ManagedThreadId;
            context.PauseAfterCommit = () =>
            {
                var rows = manager.GetSnapshot().ResourceRows;
                if (Thread.CurrentThread.ManagedThreadId != producerThread
                    || rows.Count != 1 || rows[0].State != ResourceState.Loading
                    || rows[0].WaitingCount != 1)
                    return null;

                // 只暂停生产者的入队提交返回，不阻止独立消费者执行自己的提交。
                context.PauseAfterCommit = null;
                return pause;
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
                return new LoadedAsset(new FakeAsset(resource.LocalKey), null, ReleaseAsync);
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
            public Func<PausedCompletion?>? PauseAfterCommit;
            public int CommitDepth => _commitDepth;
            public bool IsCurrent => _inner.IsCurrent;

            public void AssertAccess() => _inner.AssertAccess();
            public void Post(Action action) => _inner.Post(() => InCommit(action));
            public void Invoke(Action action) => _inner.Invoke(() => InCommit(action));
            public T Invoke<T>(Func<T> function) => _inner.Invoke(() => InCommit(function));
            public Task<T> InvokeAsync<T>(Func<Task<T>> function) => _inner.InvokeAsync(function);

            public Task RunAsync(Action action)
            {
                var operation = _inner.RunAsync(() => InCommit(action));
                var pause = operation.IsCompletedSuccessfully ? PauseAfterCommit?.Invoke() : null;
                if (pause == null) return operation;
                pause.Committed.TrySetResult(true);
                return DelayCompletion(operation, pause.Resume.Task);
            }

            public Task<T> RunAsync<T>(Func<T> function)
            {
                var operation = _inner.RunAsync(() => InCommit(function));
                var pause = operation.IsCompletedSuccessfully ? PauseAfterCommit?.Invoke() : null;
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
