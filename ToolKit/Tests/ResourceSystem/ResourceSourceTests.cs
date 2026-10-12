using System;
using System.Linq;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public class ResourceSourceTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task RegisteredSource_UsesItsImplementation()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader { Factory = _ => new FakeAsset("project-addressables") };
            manager.RegisterLoader(ResourceSource.AssetBundle, loader);
            await manager.InitializeAsync();

            var reference = await manager.LoadAsync<FakeAsset>("enemy", ResourceSource.AssetBundle);

            Assert.Equal("project-addressables", reference.Value.Name);
            Assert.Equal(1, loader.LoadCount);
            reference.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task SameAddressInDifferentSources_UsesIndependentResources()
        {
            var manager = V2Test.NewManager(out _, out _);
            var local = new FakeLoader { Factory = _ => new FakeAsset("local") };
            var remote = new FakeLoader { Factory = _ => new FakeAsset("remote") };
            manager.RegisterLoader(ResourceSource.Local, local);
            manager.RegisterLoader(ResourceSource.Remote, remote);
            await manager.InitializeAsync();

            var localReference = await manager.LoadAsync<FakeAsset>("same", ResourceSource.Local);
            var remoteReference = await manager.LoadAsync<FakeAsset>("same", ResourceSource.Remote);

            Assert.Equal("local", localReference.Value.Name);
            Assert.Equal("remote", remoteReference.Value.Name);
            Assert.Equal(2, manager.GetSnapshot().ResourceRows.Count);
            localReference.Dispose();
            remoteReference.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => local.ReleaseCount == 1 && remote.ReleaseCount == 1);
            Assert.Equal(1, local.ReleaseCount);
            Assert.Equal(1, remote.ReleaseCount);
        }

        [Fact]
        public async Task SameSourceAndAddress_ShareOneConcurrentLoadAndIndependentReferences()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader();
            manager.RegisterLoader(ResourceSource.Resources, loader);
            await manager.InitializeAsync();

            var first = manager.LoadAsync<FakeAsset>("same", ResourceSource.Resources);
            var second = manager.LoadAsync<FakeAsset>("same", ResourceSource.Resources);
            loader.ReleaseGate("same");
            var references = await Task.WhenAll(first, second).WaitAsync(TestTimeout);

            Assert.Equal(1, loader.LoadCount);
            Assert.NotSame(references[0], references[1]);
            Assert.Same(references[0].Value, references[1].Value);
            references[0].Dispose();
            Assert.True(references[1].IsValid);
            references[1].Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task ReplaceLoader_ChangesImplementationWithoutChangingBusinessSource()
        {
            var manager = V2Test.NewManager(out _, out _);
            var original = new FakeLoader();
            var replacement = new FakeLoader { Factory = _ => new FakeAsset("replacement") };
            manager.RegisterLoader(ResourceSource.AssetBundle, original);
            manager.ReplaceLoader(ResourceSource.AssetBundle, replacement);
            await manager.InitializeAsync();

            var reference = await manager.LoadAsync<FakeAsset>("same", ResourceSource.AssetBundle);

            Assert.Equal("replacement", reference.Value.Name);
            Assert.Equal(0, original.LoadCount);
            Assert.Equal(1, replacement.LoadCount);
            reference.Dispose();
            manager.Dispose();
        }

        [Fact]
        public void PublicRoutingEntryPoints_AcceptOnlyExplicitSource()
        {
            var entryPoints = typeof(ResourceManager).GetMethods()
                .Where(method => method.Name is "RegisterLoader" or "ReplaceLoader"
                    or "LoadAsync" or "RentAsync" or "ClearPoolAsync")
                .ToArray();
            Assert.NotEmpty(entryPoints);
            Assert.All(entryPoints, method =>
            {
                var source = Assert.Single(method.GetParameters()
                    .Where(parameter => parameter.ParameterType == typeof(ResourceSource)));
                Assert.False(source.IsOptional);
                Assert.False(source.HasDefaultValue);
                Assert.DoesNotContain(method.GetParameters(), parameter =>
                    parameter.ParameterType == typeof(string) && parameter.Name is "loader" or "name");
            });
            Assert.False(Enum.IsDefined(typeof(ResourceSource), 0));
        }

        [Fact]
        public async Task UnregisteredSource_FailsWithoutUsingAnotherSource()
        {
            var manager = V2Test.NewManager(out _, out _);
            var unrelatedLoader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, unrelatedLoader);
            await manager.InitializeAsync();

            var error = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                manager.LoadAsync<FakeAsset>("same", ResourceSource.Remote));

            Assert.Equal(DiagnosticCodes.LoaderNotRegistered, error.Error.DiagnosticCode);
            Assert.Equal(0, unrelatedLoader.ResolveCount);
            Assert.Equal(0, unrelatedLoader.LoadCount);
            manager.Dispose();
        }

        [Fact]
        public async Task DuplicateSourceRegistration_PreservesOriginalImplementation()
        {
            var manager = V2Test.NewManager(out _, out _);
            var existing = new FakeLoader { Factory = _ => new FakeAsset("existing") };
            var other = new FakeLoader { Factory = _ => new FakeAsset("other") };
            manager.RegisterLoader(ResourceSource.Local, existing);

            Assert.Throws<InvalidOperationException>(() => manager.RegisterLoader(ResourceSource.Local, other));
            manager.RegisterLoader(ResourceSource.Remote, other);
            await manager.InitializeAsync();

            var localReference = await manager.LoadAsync<FakeAsset>("same", ResourceSource.Local);
            var remoteReference = await manager.LoadAsync<FakeAsset>("same", ResourceSource.Remote);
            Assert.Equal("existing", localReference.Value.Name);
            Assert.Equal("other", remoteReference.Value.Name);
            localReference.Dispose();
            remoteReference.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task Running_AllowsNewSourceButRejectsReplacingExistingLoader()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var reference = await manager.LoadAsync<FakeAsset>("same", ResourceSource.Local);

            var remote = new FakeLoader { Factory = _ => new FakeAsset("remote") };
            manager.RegisterLoader(ResourceSource.Remote, remote);
            var remoteReference = await manager.LoadAsync<FakeAsset>("same", ResourceSource.Remote);
            Assert.Equal("remote", remoteReference.Value.Name);
            Assert.Throws<InvalidOperationException>(() =>
                manager.ReplaceLoader(ResourceSource.Local, new FakeLoader()));
            Assert.Equal(1, loader.LoadCount);
            reference.Dispose();
            remoteReference.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task RentAndClear_UseSameSourceForPoolIdentity()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader { Factory = key => new FakePrefab(key) };
            var factory = new FakeInstanceFactory();
            manager.RegisterLoader(ResourceSource.AssetBundle, loader);
            manager.RegisterFactory("instances", factory);
            await manager.InitializeAsync();

            var lease = await manager.RentAsync<FakeInstance>(
                "enemy", ResourceSource.AssetBundle, factory: "instances").WaitAsync(TestTimeout);
            var instance = lease.Value;
            Assert.Equal(ResourceSource.AssetBundle, factory.LastCreationRequest!.Value.Source);
            lease.Dispose();

            await manager.ClearPoolAsync(
                "enemy", ResourceSource.AssetBundle, factory: "instances").WaitAsync(TestTimeout);

            Assert.True(instance.Destroyed);
            Assert.Equal(1, factory.DestroyCount);
            Assert.Empty(manager.GetSnapshot().PoolRows);
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Theory]
        [InlineData((ResourceSource)0)]
        [InlineData((ResourceSource)999)]
        public async Task InvalidSource_IsRejectedWithoutChangingConfiguration(ResourceSource source)
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();

            Assert.Throws<ArgumentOutOfRangeException>(() => manager.RegisterLoader(source, loader));
            Assert.Throws<ArgumentOutOfRangeException>(() => manager.ReplaceLoader(source, loader));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                manager.LoadAsync<FakeAsset>("same", source));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                manager.RentAsync<FakeInstance>("same", source));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                manager.ClearPoolAsync("same", source));

            manager.RegisterLoader(ResourceSource.Local, loader);
            Assert.False(manager.GetSnapshot().IsInitialized);
            await manager.InitializeAsync();
            var reference = await manager.LoadAsync<FakeAsset>("same", ResourceSource.Local);
            Assert.Equal(1, loader.LoadCount);
            reference.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task InvalidRegistrationPolicy_LeavesSourceAvailable()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();

            Assert.Throws<ArgumentOutOfRangeException>(() => manager.RegisterLoader(
                ResourceSource.Local, loader, new LoaderPolicy { MaxConcurrentLoads = -1 }));

            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var reference = await manager.LoadAsync<FakeAsset>("same", ResourceSource.Local);
            Assert.Equal(1, loader.LoadCount);
            reference.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task InvalidReplacementPolicy_PreservesRegisteredImplementation()
        {
            var manager = V2Test.NewManager(out _, out _);
            var original = new FakeLoader();
            var replacement = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, original);

            Assert.Throws<ArgumentOutOfRangeException>(() => manager.ReplaceLoader(
                ResourceSource.Local, replacement, new LoaderPolicy { MaxConcurrentLoads = -1 }));
            await manager.InitializeAsync();

            var reference = await manager.LoadAsync<FakeAsset>("same", ResourceSource.Local);
            Assert.Equal(1, original.LoadCount);
            Assert.Equal(0, replacement.LoadCount);
            reference.Dispose();
            manager.Dispose();
        }
    }
}
