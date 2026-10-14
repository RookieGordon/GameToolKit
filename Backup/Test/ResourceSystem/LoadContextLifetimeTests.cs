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
                Assert.Empty(manager.GetSnapshot().DirtyResources);
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
                // 后端方法的同步前缀重入关闭管理器，尚未产生加载结果。
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
        public async Task LoadFailsWithoutResult_UnknownCleanupDoesNotInventDirtyObjectsOrBlockReload(CleanupStatus cleanup)
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
            Assert.Equal(cleanup, failure.Error.Cleanup);
            Assert.Empty(manager.GetSnapshot().DirtyResources);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
            using var retry = await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local).WaitAsync(TestTimeout);
            Assert.Equal(2, loader.LoadCount);
            manager.Dispose();
        }

        [Fact]
        public async Task InvalidatedHeldResult_DoesNotBlockReloadAndOldReturnCannotRemoveNewProduct()
        {
            var manager = V2Test.NewManager(out _, out _);
            var firstAlive = true;
            var firstValue = new FakeAsset("first");
            var secondValue = new FakeAsset("second");
            var released = 0;
            Task Release() { Interlocked.Increment(ref released); return Task.CompletedTask; }
            var loader = new FakeLoader
            {
                ResultOverrides = new Queue<LoadedAsset?>(new[]
                {
                    new LoadedAsset(firstValue, () => firstAlive, Release),
                    new LoadedAsset(secondValue, null, Release),
                }),
            };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var first = await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
            firstAlive = false;
            Assert.False(first.IsValid);
            Assert.Equal(DiagnosticCodes.AssetInvalidated,
                Assert.Throws<ResourceLoadException>(() => first.Value).Error.DiagnosticCode);
            var second = await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local).WaitAsync(TestTimeout);
            Assert.Same(secondValue, second.Value);
            Assert.Equal(2, loader.LoadCount);
            Assert.Equal(0, released);
            first.Dispose();
            Assert.True(second.IsValid);
            var result = await manager.UnloadUnusedAsync();
            Assert.Equal(1, result.ReleasedCount);
            Assert.Equal(1, result.SkippedInUseCount);
            using var sameSecond = await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
            Assert.Same(secondValue, sameSecond.Value);
            Assert.Equal(2, loader.LoadCount);
            second.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => released == 2);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CompletedLoad_DoesNotKeepResolutionPayloadAliveWhileResultIsHeldOrIdle(bool returnToCache)
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new PayloadLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var reference = await CompleteLoadAsync(manager);

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

        // 隔离加载调用栈，避免测试自身保留临时状态；消费者可能异步处理同步完成的后端。
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<ResourceRef<FakeAsset>> CompleteLoadAsync(ResourceManager manager)
        {
            return await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local)
                .WaitAsync(TestTimeout).ConfigureAwait(false);
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
                return Task.FromResult(new LoadedAsset(new FakeAsset(resource.LocalKey), null, ReleaseAsync));
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
                return new LoadedAsset(new FakeAsset(resource.LocalKey), null, async () =>
                {
                    ReleaseStarted.TrySetResult(true);
                    await CompleteRelease.Task.ConfigureAwait(false);
                    Interlocked.Increment(ref CompletedReleases);
                });
            }
        }
    }
}
