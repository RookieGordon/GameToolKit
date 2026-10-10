using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityToolKit.Runtime.Resource;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public class ResourceAcquisitionFlowTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task LoadDuringUnload_WaitsForReleaseThenResolvesCurrentVersion()
        {
            var manager = V2Test.NewManager(out _, out _,
                options => options.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.Zero });
            var loader = new VersionedReleaseLoader(key => new FakeAsset(key));
            manager.RegisterLoader("fake", loader);

            var first = await manager.LoadAsync<FakeAsset>("asset").WaitAsync(TestTimeout);
            var firstValue = first.Value;
            first.Dispose();
            await loader.FirstReleaseStarted.Task.WaitAsync(TestTimeout);

            var pending = manager.LoadAsync<FakeAsset>("asset");
            Assert.False(pending.IsCompleted);
            Assert.Equal(2, loader.ResolveCount);
            Assert.Single(loader.LoadedResources);

            loader.CurrentKey = "asset#v2";
            loader.FinishFirstRelease.TrySetResult(true);
            var second = await pending.WaitAsync(TestTimeout);

            Assert.Equal(3, loader.ResolveCount);
            Assert.Equal("asset#v2", second.Value.Name);
            Assert.NotSame(firstValue, second.Value);
            Assert.Equal(new[] { "asset#v1", "asset#v2" },
                Array.ConvertAll(loader.LoadedResources.ToArray(), resource => resource.LocalKey));
            second.Dispose();
            await manager.ShutdownAsync().WaitAsync(TestTimeout);
            Assert.Equal(2, loader.ReleaseCount);
        }

        [Fact]
        public async Task RentDuringPrototypeUnload_WaitsForReleaseAndKeepsResolvedVersion()
        {
            var manager = V2Test.NewManager(out _, out _,
                options => options.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.Zero });
            var loader = new VersionedReleaseLoader(key => new FakePrefab(key));
            var factory = new FakeInstanceFactory();
            manager.RegisterLoader("fake", loader);
            manager.RegisterFactory("fakeFactory", factory);

            var first = await manager.LoadAsync<FakePrefab>("asset").WaitAsync(TestTimeout);
            var firstValue = first.Value;
            first.Dispose();
            await loader.FirstReleaseStarted.Task.WaitAsync(TestTimeout);

            var pending = manager.RentAsync<FakeInstance>("asset", factory: "fakeFactory");
            Assert.False(pending.IsCompleted);
            Assert.Equal(2, loader.ResolveCount);
            Assert.Single(loader.LoadedResources);
            Assert.Equal(0, factory.CreateCount);
            var prototypeResolution = loader.ResolvedResources.ToArray()[1];

            loader.CurrentKey = "asset#v2";
            loader.FinishFirstRelease.TrySetResult(true);
            var lease = await pending.WaitAsync(TestTimeout);

            Assert.Equal(2, loader.ResolveCount);
            Assert.Equal("asset#v1", lease.Value.Proto.Name);
            Assert.NotSame(firstValue, lease.Value.Proto);
            Assert.Same(prototypeResolution, loader.LoadedResources.ToArray()[1]);
            lease.Dispose();
            await manager.ShutdownAsync().WaitAsync(TestTimeout);
            Assert.Equal(2, loader.ReleaseCount);
            Assert.Equal(1, factory.DestroyCount);
        }

        [Fact]
        public async Task SharedLoad_DifferentRequestedTypes_OnlyCompatibleWaitersReceiveHolds()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader();
            manager.RegisterLoader("fake", loader);

            var typedTask = manager.LoadAsync<FakeAsset>("asset");
            var wrongTypeTask = manager.LoadAsync<string>("asset");
            var objectTask = manager.LoadAsync<object>("asset");
            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(3, Assert.Single(manager.GetSnapshot().ResourceRows).WaitingCount);

            loader.ReleaseGate("asset");
            var typed = await typedTask.WaitAsync(TestTimeout);
            var boxed = await objectTask.WaitAsync(TestTimeout);
            var error = await Assert.ThrowsAsync<ResourceLoadException>(
                () => wrongTypeTask.WaitAsync(TestTimeout));

            Assert.Equal(DiagnosticCodes.AssetTypeMismatch, error.Error.DiagnosticCode);
            Assert.Same(typed.Value, boxed.Value);
            Assert.NotSame((object)typed, boxed);
            var row = Assert.Single(manager.GetSnapshot().ResourceRows);
            Assert.Equal(2, row.HoldCount);
            Assert.Equal(0, row.WaitingCount);
            Assert.Equal(1, loader.LoadCount);

            typed.Dispose();
            Assert.True(boxed.IsValid);
            Assert.Equal(1, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);
            boxed.Dispose();
            await manager.ShutdownAsync().WaitAsync(TestTimeout);
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public void CancellationBeforeQueuedSuccessCommit_ReleasesResultWithoutGrantingHold()
        {
            using var pump = new MainThreadTestPump();
            var manager = new ResourceManager(new UnityExecutionContext(),
                new ResourceSystemOptions { RequestTimeout = null, DefaultLoader = "fake" });
            var loader = new GatedLoader();
            manager.RegisterLoader("fake", loader);
            using var cancellation = new CancellationTokenSource();

            var pending = manager.LoadAsync<FakeAsset>("asset", cancellationToken: cancellation.Token);
            Assert.Equal(1, loader.LoadCount);
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);

            // The loader has succeeded, but its main-thread result commit has not run yet.
            loader.ReleaseGate("asset");
            pump.WaitForPost();
            cancellation.Cancel();
            pump.RunUntil(() => pending.IsCompleted);
            Assert.ThrowsAny<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
            pump.RunUntil(() => loader.ReleaseCount == 1 && manager.GetSnapshot().ResourceRows.Count == 0);

            Assert.Equal(1, loader.ReleaseCount);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
            var shutdown = manager.ShutdownAsync();
            pump.RunUntil(() => shutdown.IsCompleted);
            shutdown.GetAwaiter().GetResult();
            Assert.Equal(ManagerState.Closed, manager.GetSnapshot().State);
        }

        private sealed class VersionedReleaseLoader : IResourceLoader
        {
            private readonly Func<string, object> _create;
            private int _releaseCount;
            private int _resolveCount;

            public VersionedReleaseLoader(Func<string, object> create)
            {
                _create = create;
            }

            public string CurrentKey = "asset#v1";
            public int ResolveCount => Volatile.Read(ref _resolveCount);
            public int ReleaseCount => Volatile.Read(ref _releaseCount);
            public readonly ConcurrentQueue<ResolvedResource> ResolvedResources = new();
            public readonly ConcurrentQueue<ResolvedResource> LoadedResources = new();
            public readonly TaskCompletionSource<bool> FirstReleaseStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> FinishFirstRelease =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<ResolvedResource> ResolveAsync(
                ResourceRequest request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _resolveCount);
                var resolved = new ResolvedResource(CurrentKey, typeof(object));
                ResolvedResources.Enqueue(resolved);
                return Task.FromResult(resolved);
            }

            public Task<LoadedAsset> LoadAsync(
                ResolvedResource resource, IProgress<ResourceProgress> progress,
                CancellationToken operationToken)
            {
                LoadedResources.Enqueue(resource);
                return Task.FromResult(new LoadedAsset(_create(resource.LocalKey), 1, null, async () =>
                {
                    if (Interlocked.Increment(ref _releaseCount) == 1)
                    {
                        FirstReleaseStarted.TrySetResult(true);
                        await FinishFirstRelease.Task.ConfigureAwait(false);
                    }
                }));
            }
        }

    }
}
