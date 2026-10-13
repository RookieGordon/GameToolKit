using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class ResourceDisposeTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task Dispose_WithHeldReference_StartsOneReleaseAndReturnsBeforeReleaseCompletes()
        {
            var manager = V2Test.NewManager(out _, out _);
            var releaseStarted = Signal();
            var allowRelease = Signal();
            var releaseFinished = Signal();
            var releaseCount = 0;
            var loader = new FakeLoader
            {
                ResultOverrides = new Queue<LoadedAsset?>(new[]
                {
                    new LoadedAsset(new FakeAsset("native"), null, async () =>
                    {
                        Interlocked.Increment(ref releaseCount);
                        releaseStarted.TrySetResult(true);
                        await allowRelease.Task.ConfigureAwait(false);
                        releaseFinished.TrySetResult(true);
                    }),
                }),
            };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var reference = await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local).WaitAsync(TestTimeout);

            try
            {
                await Task.Run(manager.Dispose).WaitAsync(TestTimeout);
                Assert.True(manager.GetSnapshot().IsDisposed);
                Assert.False(reference.IsValid);
                Assert.Throws<ObjectDisposedException>(() => reference.Value);
                Assert.Throws<ObjectDisposedException>(() => reference.Retain());
                await releaseStarted.Task.WaitAsync(TestTimeout);
                Assert.False(releaseFinished.Task.IsCompleted);

                manager.Dispose();
                reference.Dispose();
                reference.Dispose();
                Assert.Equal(1, releaseCount);
                var rejected = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                    manager.LoadAsync<FakeAsset>("other", ResourceSource.Local).WaitAsync(TestTimeout));
                Assert.Equal(DiagnosticCodes.LifecycleManagerDisposed, rejected.Error.DiagnosticCode);

                allowRelease.TrySetResult(true);
                await releaseFinished.Task.WaitAsync(TestTimeout);
                Assert.Equal(1, releaseCount);
            }
            finally
            {
                allowRelease.TrySetResult(true);
                reference.Dispose();
                manager.Dispose();
            }
        }

        [Fact]
        public async Task Dispose_WithHeldLease_DestroysInstanceBeforeReleasingHeldPrototype()
        {
            var manager = V2Test.NewManager(out _, out _);
            var factory = new ControlledFactory(pauseCreation: false);
            var prototypeReleased = Signal();
            var destroyCountAtRelease = -1;
            var releaseCount = 0;
            var loader = PrefabLoader(() =>
            {
                destroyCountAtRelease = factory.Inner.DestroyCount;
                Interlocked.Increment(ref releaseCount);
                prototypeReleased.TrySetResult(true);
            });
            manager.RegisterLoader(ResourceSource.Local, loader);
            manager.RegisterFactory("instances", factory);
            await manager.InitializeAsync();
            var prototype = await manager.LoadAsync<FakePrefab>("asset", ResourceSource.Local).WaitAsync(TestTimeout);
            var lease = await manager.RentAsync<FakeInstance>(
                "asset", ResourceSource.Local, factory: "instances").WaitAsync(TestTimeout);
            var instance = lease.Value;

            try
            {
                manager.Dispose();
                Assert.False(lease.IsValid);
                Assert.False(prototype.IsValid);
                Assert.Throws<ObjectDisposedException>(() => lease.Value);
                await factory.DestroyStarted.Task.WaitAsync(TestTimeout);
                Assert.Equal(0, releaseCount);
                Assert.False(instance.Destroyed);

                // 业务迟到归还不能重复排入销毁，也不能提早释放原型。
                lease.Dispose();
                prototype.Dispose();
                manager.Dispose();
                Assert.Equal(0, releaseCount);
                factory.AllowDestroy.TrySetResult(true);
                await prototypeReleased.Task.WaitAsync(TestTimeout);
                Assert.True(instance.Destroyed);
                Assert.Equal(1, factory.Inner.DestroyCount);
                Assert.Equal(1, destroyCountAtRelease);
                Assert.Equal(1, releaseCount);
            }
            finally
            {
                factory.AllowDestroy.TrySetResult(true);
                lease.Dispose();
                prototype.Dispose();
                manager.Dispose();
            }
        }

        [Fact]
        public async Task Dispose_DuringCreation_DestroysLateInstanceBeforeReleasingPrototype()
        {
            var manager = V2Test.NewManager(out _, out _);
            var factory = new ControlledFactory(pauseCreation: true);
            var prototypeReleased = Signal();
            var destroyCountAtRelease = -1;
            var loader = PrefabLoader(() =>
            {
                destroyCountAtRelease = factory.Inner.DestroyCount;
                prototypeReleased.TrySetResult(true);
            });
            manager.RegisterLoader(ResourceSource.Local, loader);
            manager.RegisterFactory("instances", factory);
            await manager.InitializeAsync();
            var pending = manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, factory: "instances");

            try
            {
                await factory.CreateStarted.Task.WaitAsync(TestTimeout);
                manager.Dispose();
                var failure = await Assert.ThrowsAsync<ResourceLoadException>(() => pending.WaitAsync(TestTimeout));
                Assert.Equal(DiagnosticCodes.LifecycleManagerDisposed, failure.Error.DiagnosticCode);
                Assert.False(prototypeReleased.Task.IsCompleted);

                factory.AllowCreate.TrySetResult(true);
                await factory.DestroyStarted.Task.WaitAsync(TestTimeout);
                Assert.False(prototypeReleased.Task.IsCompleted);
                factory.AllowDestroy.TrySetResult(true);
                await prototypeReleased.Task.WaitAsync(TestTimeout);
                Assert.True(factory.Created!.Destroyed);
                Assert.Equal(1, factory.Inner.CreateCount);
                Assert.Equal(1, factory.Inner.DestroyCount);
                Assert.Equal(1, destroyCountAtRelease);
            }
            finally
            {
                factory.AllowCreate.TrySetResult(true);
                factory.AllowDestroy.TrySetResult(true);
                manager.Dispose();
            }
        }

        [Fact]
        public async Task Dispose_OneDestroyFails_PrototypeWaitsForOtherDestroyToFinish()
        {
            var manager = V2Test.NewManager(out _, out var diagnostics);
            var factory = new ControlledFactory(pauseCreation: false);
            var prototypeReleased = Signal();
            manager.RegisterLoader(ResourceSource.Local, PrefabLoader(() => prototypeReleased.TrySetResult(true)));
            manager.RegisterFactory("instances", factory);
            await manager.InitializeAsync();
            var first = await manager.RentAsync<FakeInstance>(
                "asset", ResourceSource.Local, factory: "instances").WaitAsync(TestTimeout);
            var firstInstance = first.Value;
            var second = await manager.RentAsync<FakeInstance>(
                "asset", ResourceSource.Local, factory: "instances").WaitAsync(TestTimeout);
            var secondDestroyStarted = Signal();
            var finishSecondDestroy = Signal();
            factory.DestroyOverride = async instance =>
            {
                if (ReferenceEquals(instance, firstInstance))
                    throw new InvalidOperationException("first destroy failed");
                secondDestroyStarted.TrySetResult(true);
                await finishSecondDestroy.Task.ConfigureAwait(false);
                await factory.Inner.DestroyAsync(instance).ConfigureAwait(false);
            };

            try
            {
                manager.Dispose();
                await secondDestroyStarted.Task.WaitAsync(TestTimeout);
                await V2Test.WaitUntilAsync(() => diagnostics.CountByCode(DiagnosticCodes.InstanceDestroyFailed) == 1);
                Assert.False(prototypeReleased.Task.IsCompleted);
                Assert.False(first.IsValid);
                Assert.False(second.IsValid);

                finishSecondDestroy.TrySetResult(true);
                await prototypeReleased.Task.WaitAsync(TestTimeout);
                Assert.Equal(1, factory.Inner.DestroyCount);
            }
            finally
            {
                finishSecondDestroy.TrySetResult(true);
                first.Dispose();
                second.Dispose();
                manager.Dispose();
            }
        }

        [Fact]
        public async Task OnReturnDisposesManager_PrototypeRemainsAliveUntilCallbackAndDestroyFinish()
        {
            var manager = V2Test.NewManager(out _, out _);
            var factory = new ControlledFactory(pauseCreation: false);
            factory.AllowDestroy.TrySetResult(true);
            var prototypeReleased = Signal();
            manager.RegisterLoader(ResourceSource.Local, PrefabLoader(() => prototypeReleased.TrySetResult(true)));
            manager.RegisterFactory("instances", factory);
            await manager.InitializeAsync();
            var lease = await manager.RentAsync<FakeInstance>(
                "asset", ResourceSource.Local, factory: "instances").WaitAsync(TestTimeout);
            var releasedInsideCallback = false;
            var destroyedInsideCallback = -1;
            factory.OnReturnAction = () =>
            {
                manager.Dispose();
                releasedInsideCallback = prototypeReleased.Task.IsCompleted;
                destroyedInsideCallback = factory.Inner.DestroyCount;
            };

            lease.Dispose();
            await prototypeReleased.Task.WaitAsync(TestTimeout);
            Assert.False(releasedInsideCallback);
            Assert.Equal(0, destroyedInsideCallback);
            Assert.Equal(1, factory.Inner.DestroyCount);
            Assert.False(lease.IsValid);
        }

        [Fact]
        public async Task Dispose_ReleaseFailureDoesNotPreventOtherResourceCleanup()
        {
            var manager = V2Test.NewManager(out _, out var diagnostics);
            var healthyReleased = Signal();
            var failedReleases = 0;
            var loader = new FakeLoader
            {
                ResultOverrides = new Queue<LoadedAsset?>(new[]
                {
                    new LoadedAsset(new FakeAsset("broken"), null, () =>
                    {
                        Interlocked.Increment(ref failedReleases);
                        return Task.FromException(new InvalidOperationException("native release failed"));
                    }),
                    new LoadedAsset(new FakeAsset("healthy"), null, () =>
                    {
                        healthyReleased.TrySetResult(true);
                        return Task.CompletedTask;
                    }),
                }),
            };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var broken = await manager.LoadAsync<FakeAsset>("broken", ResourceSource.Local).WaitAsync(TestTimeout);
            var healthy = await manager.LoadAsync<FakeAsset>("healthy", ResourceSource.Local).WaitAsync(TestTimeout);

            manager.Dispose();
            await healthyReleased.Task.WaitAsync(TestTimeout);
            await V2Test.WaitUntilAsync(() => diagnostics.CountByCode(DiagnosticCodes.LifecycleReleaseFailed) == 1);
            Assert.False(broken.IsValid);
            Assert.False(healthy.IsValid);
            Assert.Equal(1, failedReleases);
            broken.Dispose();
            healthy.Dispose();
            manager.Dispose();
            Assert.Equal(1, failedReleases);
        }

        [Fact]
        public async Task OrdinaryReleaseFailure_DoesNotDisposeManagerOrBlockOtherResource()
        {
            var manager = V2Test.NewManager(out _, out var diagnostics);
            var healthyReleased = Signal();
            var loader = new FakeLoader
            {
                ResultOverrides = new Queue<LoadedAsset?>(new[]
                {
                    new LoadedAsset(new FakeAsset("broken"), null,
                        () => Task.FromException(new InvalidOperationException("native release failed"))),
                    new LoadedAsset(new FakeAsset("healthy"), null, () =>
                    {
                        healthyReleased.TrySetResult(true);
                        return Task.CompletedTask;
                    }),
                }),
            };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var broken = await manager.LoadAsync<FakeAsset>("broken", ResourceSource.Local).WaitAsync(TestTimeout);
            broken.Dispose();
            await manager.UnloadUnusedAsync();
            await V2Test.WaitUntilAsync(() => diagnostics.CountByCode(DiagnosticCodes.LifecycleReleaseFailed) == 1);
            Assert.True(manager.GetSnapshot().IsInitialized);
            Assert.False(manager.GetSnapshot().IsDisposed);

            var healthy = await manager.LoadAsync<FakeAsset>("healthy", ResourceSource.Local).WaitAsync(TestTimeout);
            Assert.Equal("healthy", healthy.Value.Name);
            healthy.Dispose();
            await manager.UnloadUnusedAsync();
            await healthyReleased.Task.WaitAsync(TestTimeout);
            Assert.False(manager.GetSnapshot().IsDisposed);
            manager.Dispose();
        }

        private static FakeLoader PrefabLoader(Action release) => new()
        {
            ResultOverrides = new Queue<LoadedAsset?>(new[]
            {
                new LoadedAsset(new FakePrefab("asset"), null, () =>
                {
                    release();
                    return Task.CompletedTask;
                }),
            }),
        };

        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class ControlledFactory : IInstanceFactory
        {
            public readonly FakeInstanceFactory Inner = new();
            public readonly TaskCompletionSource<bool> CreateStarted = Signal();
            public readonly TaskCompletionSource<bool> AllowCreate = Signal();
            public readonly TaskCompletionSource<bool> DestroyStarted = Signal();
            public readonly TaskCompletionSource<bool> AllowDestroy = Signal();
            public FakeInstance? Created;
            public Func<object, Task>? DestroyOverride;
            public Action? OnReturnAction;

            public ControlledFactory(bool pauseCreation)
            {
                if (!pauseCreation) AllowCreate.TrySetResult(true);
            }

            public ResourceRequest GetPrototypeRequest(ResourceRequest request) => Inner.GetPrototypeRequest(request);
            public string GetInstanceKey(ResourceRequest request) => Inner.GetInstanceKey(request);
            public bool CanCreate(object prototype, Type instanceType) => Inner.CanCreate(prototype, instanceType);
            public bool IsAlive(object instance) => Inner.IsAlive(instance);
            public void OnRent(object instance) => Inner.OnRent(instance);
            public void OnReturn(object instance)
            {
                OnReturnAction?.Invoke();
                Inner.OnReturn(instance);
            }

            public async Task<object> CreateAsync(object prototype, ResourceRequest request, CancellationToken token)
            {
                Created = (FakeInstance)await Inner.CreateAsync(prototype, request, token).ConfigureAwait(false);
                CreateStarted.TrySetResult(true);
                await AllowCreate.Task.ConfigureAwait(false); // 模拟无法立即取消的创建操作。
                return Created;
            }

            public async Task DestroyAsync(object instance)
            {
                if (DestroyOverride != null)
                {
                    await DestroyOverride(instance).ConfigureAwait(false);
                    return;
                }
                DestroyStarted.TrySetResult(true);
                await AllowDestroy.Task.ConfigureAwait(false);
                await Inner.DestroyAsync(instance).ConfigureAwait(false);
            }
        }
    }
}
