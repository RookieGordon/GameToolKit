using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class LoadContextLifetimeTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task AllRequestsCancel_DisposeReturnsAndLateResultStillFinishesRelease()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new LateResultLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            using var cancellation = new CancellationTokenSource();

            try
            {
                var first = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local,
                    cancellationToken: cancellation.Token);
                await loader.LoadStarted.Task.WaitAsync(TestTimeout);
                var second = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local,
                    cancellationToken: cancellation.Token);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TestTimeout));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TestTimeout));

                manager.Dispose();
                Assert.True(manager.GetSnapshot().IsDisposed);

                loader.CompleteLoad.TrySetResult(true);
                await loader.ReleaseStarted.Task.WaitAsync(TestTimeout);
                Assert.Equal(0, loader.CompletedReleases);

                loader.CompleteRelease.TrySetResult(true);
                await V2Test.WaitUntilAsync(() => loader.CompletedReleases == 1
                    && manager.GetSnapshot().ResourceRows.Count == 0);
                Assert.Equal(1, loader.LoadCount);
                Assert.Equal(1, loader.CompletedReleases);
                Assert.True(manager.GetSnapshot().IsDisposed);
                Assert.Empty(manager.GetSnapshot().ResourceRows);
                Assert.Empty(manager.GetSnapshot().CleanupErrors);
            }
            finally
            {
                loader.CompleteLoad.TrySetResult(true);
                loader.CompleteRelease.TrySetResult(true);
                manager.Dispose();
            }
        }

        [Fact]
        public async Task LoaderSynchronouslyDisposesManager_LateResultIsReleased()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new LateResultLoader { OnLoadStarted = manager.Dispose };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            try
            {
                // 在 LoadAsync 返回任务之前，后端同步重入关闭管理器。
                var pending = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
                var failure = await Assert.ThrowsAsync<ResourceLoadException>(() => pending.WaitAsync(TestTimeout));
                Assert.Equal(DiagnosticCodes.LifecycleManagerDisposed, failure.Error.DiagnosticCode);
                Assert.True(manager.GetSnapshot().IsDisposed);

                loader.CompleteLoad.TrySetResult(true);
                await loader.ReleaseStarted.Task.WaitAsync(TestTimeout);
                Assert.Equal(0, loader.CompletedReleases);

                loader.CompleteRelease.TrySetResult(true);
                await V2Test.WaitUntilAsync(() => loader.CompletedReleases == 1
                    && manager.GetSnapshot().ResourceRows.Count == 0);
                Assert.Equal(1, loader.LoadCount);
                Assert.Equal(1, loader.CompletedReleases);
                Assert.True(manager.GetSnapshot().IsDisposed);
                Assert.Empty(manager.GetSnapshot().ResourceRows);
            }
            finally
            {
                loader.CompleteLoad.TrySetResult(true);
                loader.CompleteRelease.TrySetResult(true);
                manager.Dispose();
            }
        }

        [Theory]
        [InlineData(CleanupStatus.Unknown)]
        [InlineData(CleanupStatus.Incomplete)]
        public async Task LoadFailsWithoutResult_UnfinishedCleanupRemainsVisibleAndBlocksReload(CleanupStatus cleanup)
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader
            {
                LoadErrors = new Queue<Exception>(new[]
                {
                    new ResourceLoadException(new LoadError(
                        DiagnosticCodes.AssetLoadFailed, LoadStage.LoadAsset, cleanup)),
                }),
            };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var failure = await Assert.ThrowsAsync<ResourceLoadException>(
                () => manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local).WaitAsync(TestTimeout));
            var recorded = Assert.Single(manager.GetSnapshot().CleanupErrors);
            Assert.Equal(failure.Error.DiagnosticId, recorded.DiagnosticId);
            Assert.Equal(cleanup, recorded.Cleanup);
            Assert.Equal(0, loader.ReleaseCount);

            var retry = await Assert.ThrowsAsync<ResourceLoadException>(
                () => manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local).WaitAsync(TestTimeout));
            Assert.Equal(recorded.DiagnosticId, retry.Error.DiagnosticId);
            Assert.Equal(1, loader.LoadCount);

            manager.Dispose();
            var closed = manager.GetSnapshot();
            Assert.True(closed.IsDisposed);
            Assert.Equal(recorded.DiagnosticId, Assert.Single(closed.CleanupErrors).DiagnosticId);
        }

        [Fact]
        public async Task InvalidatedHeldResult_IsUnavailableAndBlocksReloadUntilOwnerReturnsIt()
        {
            var manager = V2Test.NewManager(out _, out _,
                options => options.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.Zero });
            var firstAlive = true;
            var firstValue = new FakeAsset("first");
            var secondValue = new FakeAsset("second");
            var released = 0;
            Task Release()
            {
                Interlocked.Increment(ref released);
                return Task.CompletedTask;
            }
            var loader = new FakeLoader
            {
                ResultOverrides = new Queue<LoadedAsset?>(new[]
                {
                    new LoadedAsset(firstValue, 1, () => firstAlive, Release),
                    new LoadedAsset(secondValue, 1, null, Release),
                }),
            };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var first = await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local).WaitAsync(TestTimeout);
            Assert.True(first.IsValid);
            firstAlive = false;
            Assert.False(first.IsValid);
            var invalid = Assert.Throws<ResourceLoadException>(() => first.Value);
            Assert.Equal(DiagnosticCodes.AssetInvalidated, invalid.Error.DiagnosticCode);

            var pending = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(0, released);

            first.Dispose();
            var second = await pending.WaitAsync(TestTimeout);
            Assert.Equal(2, loader.LoadCount);
            Assert.Equal(1, released);
            Assert.Same(secondValue, second.Value);
            first.Dispose();
            Assert.True(second.IsValid);
            Assert.Equal(1, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);

            second.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => released == 2);
            Assert.Equal(2, released);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CompletedLoad_DoesNotKeepResolutionPayloadAliveWhileResultIsHeldOrIdle(bool returnToCache)
        {
            var manager = V2Test.NewManager(out _, out _,
                options => options.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.FromMinutes(1) });
            var loader = new PayloadLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var reference = CompleteImmediateLoad(manager);

            try
            {
                if (returnToCache)
                    reference.Dispose();

                // 解析数据只服务于加载；保留产物或已归还的凭证，不应使它继续存活。
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.False(loader.Payload!.IsAlive);
                var row = Assert.Single(manager.GetSnapshot().ResourceRows);
                Assert.Equal(returnToCache ? ResourceState.Idle : ResourceState.Ready, row.State);
                Assert.Equal(returnToCache ? 0 : 1, row.HoldCount);
                Assert.Equal(0, loader.ReleaseCount);
                if (!returnToCache)
                    Assert.Equal("asset", reference.Value.Name);
                GC.KeepAlive(reference);
                GC.KeepAlive(manager);
            }
            finally
            {
                reference.Dispose();
                manager.Dispose();
                await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            }
            Assert.Equal(1, loader.ReleaseCount);
        }

        // 假加载器同步完成；隔离调用栈，避免测试自身的已完成加载任务延长临时数据寿命。
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ResourceRef<FakeAsset> CompleteImmediateLoad(ResourceManager manager)
        {
            return manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local)
                .WaitAsync(TestTimeout).GetAwaiter().GetResult();
        }

        private sealed class PayloadLoader : IResourceLoader
        {
            public WeakReference? Payload;
            public int ReleaseCount;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
            {
                var payload = new object();
                Payload = new WeakReference(payload);
                return Task.FromResult(new ResolvedResource(request.Address, typeof(FakeAsset), payload));
            }

            public Task<LoadedAsset> LoadAsync(ResolvedResource resource,
                IProgress<ResourceProgress> progress, CancellationToken operationToken)
            {
                Assert.NotNull(resource.Payload);
                return Task.FromResult(new LoadedAsset(new FakeAsset(resource.LocalKey), 1, null, ReleaseAsync));
            }

            private Task ReleaseAsync()
            {
                Interlocked.Increment(ref ReleaseCount);
                return Task.CompletedTask;
            }
        }

        private sealed class LateResultLoader : IResourceLoader
        {
            public readonly TaskCompletionSource<bool> LoadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> CompleteLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ReleaseStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> CompleteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int LoadCount;
            public int CompletedReleases;
            public Action? OnLoadStarted;

            public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new ResolvedResource(request.Address, typeof(FakeAsset)));
            }

            public async Task<LoadedAsset> LoadAsync(ResolvedResource resource,
                IProgress<ResourceProgress> progress, CancellationToken operationToken)
            {
                Interlocked.Increment(ref LoadCount);
                OnLoadStarted?.Invoke();
                LoadStarted.TrySetResult(true);
                // 模拟已经无法取消的底层操作。
                await CompleteLoad.Task.ConfigureAwait(false);
                return new LoadedAsset(new FakeAsset(resource.LocalKey), 1, null, async () =>
                {
                    ReleaseStarted.TrySetResult(true);
                    await CompleteRelease.Task.ConfigureAwait(false);
                    Interlocked.Increment(ref CompletedReleases);
                });
            }
        }
    }
}
