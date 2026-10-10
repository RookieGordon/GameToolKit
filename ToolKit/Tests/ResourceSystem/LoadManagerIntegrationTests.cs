using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public class LoadManagerIntegrationTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task RentWithMappedPrototype_UsesTargetLoaderAndClearsByBusinessAddress()
        {
            var manager = V2Test.NewManager(out _, out _,
                options => options.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.Zero });
            var businessLoader = new FakeLoader();
            var prototypeLoader = new FakeLoader { Factory = key => new FakePrefab(key) };
            var factory = new MappedPrototypeFactory("prefabs", "prefabs/enemy");
            manager.RegisterLoader("business", businessLoader);
            manager.RegisterLoader("prefabs", prototypeLoader);
            manager.RegisterFactory("mapped", factory);

            var lease = await manager.RentAsync<FakeInstance>(
                "enemy", loader: "business", factory: "mapped").WaitAsync(TestTimeout);
            var instance = lease.Value;

            Assert.Equal("prefabs/enemy", instance.Proto.Name);
            Assert.Equal(0, businessLoader.ResolveCount);
            Assert.Equal(0, businessLoader.LoadCount);
            Assert.Equal(1, prototypeLoader.ResolveCount);
            Assert.Equal(1, prototypeLoader.LoadCount);
            Assert.Equal("business", factory.Inner.LastCreationRequest!.Value.LoaderId);
            Assert.Equal("enemy", factory.Inner.LastCreationRequest!.Value.Address);

            lease.Dispose();
            await manager.ClearPoolAsync(
                "enemy", loader: "business", factory: "mapped").WaitAsync(TestTimeout);

            Assert.True(instance.Destroyed);
            Assert.Equal(1, factory.Inner.DestroyCount);
            Assert.Empty(manager.GetSnapshot().PoolRows);
            await manager.ShutdownAsync().WaitAsync(TestTimeout);
            Assert.Equal(1, prototypeLoader.ReleaseCount);
            Assert.Equal(0, businessLoader.ReleaseCount);
        }

        [Fact]
        public async Task LoadAndRentSamePrototype_ShareLoadingAndHoldUntilInstanceDestructionFinishes()
        {
            var manager = V2Test.NewManager(out _, out _,
                options => options.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.Zero });
            var loader = new GatedLoader { Factory = key => new FakePrefab(key) };
            var destructionFinished = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new FakeInstanceFactory { DestroyGate = destructionFinished };
            manager.RegisterLoader("fake", loader);
            manager.RegisterFactory("instances", factory);

            var load = manager.LoadAsync<FakePrefab>("enemy");
            var rent = manager.RentAsync<FakeInstance>("enemy", factory: "instances");
            Assert.False(load.IsCompleted);
            Assert.False(rent.IsCompleted);
            Assert.Equal(1, loader.LoadCount);

            loader.ReleaseGate("enemy");
            var reference = await load.WaitAsync(TestTimeout);
            var lease = await rent.WaitAsync(TestTimeout);
            var instance = lease.Value;
            Assert.Same(reference.Value, instance.Proto);
            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(2, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);

            reference.Dispose();
            Assert.True(lease.IsValid);
            Assert.Equal(1, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);
            Assert.Equal(0, loader.ReleaseCount);

            lease.Dispose();
            var clear = manager.ClearPoolAsync("enemy", factory: "instances");
            Assert.False(clear.IsCompleted);
            Assert.Equal(1, Assert.Single(manager.GetSnapshot().PoolRows).InFlight);
            Assert.Equal(1, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);
            Assert.False(instance.Destroyed);
            Assert.Equal(0, loader.ReleaseCount);

            destructionFinished.TrySetResult(true);
            await clear.WaitAsync(TestTimeout);
            await manager.ShutdownAsync().WaitAsync(TestTimeout);
            Assert.True(instance.Destroyed);
            Assert.Equal(1, factory.DestroyCount);
            Assert.Equal(1, loader.ReleaseCount);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
        }

        private sealed class MappedPrototypeFactory : IInstanceFactory
        {
            private readonly string _loader;
            private readonly string _address;
            public readonly FakeInstanceFactory Inner = new();

            public MappedPrototypeFactory(string loader, string address)
            {
                _loader = loader;
                _address = address;
            }

            public ResourceRequest GetPrototypeRequest(ResourceRequest instanceRequest) =>
                new(_loader, _address, typeof(FakePrefab), instanceRequest.Parameters);

            public string GetInstanceKey(ResourceRequest instanceRequest) => Inner.GetInstanceKey(instanceRequest);
            public bool CanCreate(object prototype, Type instanceType) => Inner.CanCreate(prototype, instanceType);
            public Task<object> CreateAsync(object prototype, ResourceRequest creationRequest,
                CancellationToken operationToken) => Inner.CreateAsync(prototype, creationRequest, operationToken);
            public bool IsAlive(object instance) => Inner.IsAlive(instance);
            public void OnRent(object instance) => Inner.OnRent(instance);
            public void OnReturn(object instance) => Inner.OnReturn(instance);
            public Task DestroyAsync(object instance) => Inner.DestroyAsync(instance);
        }
    }
}
