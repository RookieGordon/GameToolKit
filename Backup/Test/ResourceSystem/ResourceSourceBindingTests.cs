using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public class ResourceSourceBindingTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task ApplyBySource_KeepsOldReferenceUntilReplacement()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var binder = new ResourceBinder(manager);
            var target = new Target();
            var applicator = new Applicator();

            loader.ReleaseGate("old");
            Assert.Equal(BindingResult.Applied, await binder.ApplyAsync(
                target, "asset", "old", applicator, ResourceSource.Local).WaitAsync(TestTimeout));
            var old = target.Asset;

            var replacement = binder.ApplyAsync(
                target, "asset", "new", applicator, ResourceSource.Local);
            Assert.False(replacement.IsCompleted);
            Assert.Same(old, target.Asset);
            Assert.Equal(0, loader.ReleaseCount);

            loader.ReleaseGate("new");
            Assert.Equal(BindingResult.Applied, await replacement.WaitAsync(TestTimeout));
            Assert.Equal("new", target.Asset!.Name);
            var unloaded = await manager.UnloadUnusedAsync();
            Assert.Equal(1, unloaded.ReleasedCount);
            Assert.Equal(1, unloaded.SkippedInUseCount);
            Assert.Equal(1, loader.ReleaseCount);
            Assert.Equal(1, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);

            binder.Revert(target, "asset", applicator);
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 2);
            Assert.Null(target.Asset);
            Assert.Equal(2, loader.ReleaseCount);
        }

        [Fact]
        public async Task CancelApplyBySource_KeepsCurrentBindingAndItsReference()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader();
            manager.RegisterLoader(ResourceSource.Remote, loader);
            await manager.InitializeAsync();
            var binder = new ResourceBinder(manager);
            var target = new Target();
            var applicator = new Applicator();

            loader.ReleaseGate("old");
            await binder.ApplyAsync(target, "asset", "old", applicator, ResourceSource.Remote)
                .WaitAsync(TestTimeout);
            var old = target.Asset;

            using var cancellation = new CancellationTokenSource();
            var replacement = binder.ApplyAsync(target, "asset", "new", applicator,
                ResourceSource.Remote, cancellationToken: cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => replacement.WaitAsync(TestTimeout));
            Assert.Same(old, target.Asset);
            Assert.Equal(0, loader.ReleaseCount);

            binder.Revert(target, "asset", applicator);
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount);
        }

        private sealed class Target
        {
            public FakeAsset? Asset;
        }

        private sealed class Applicator : IResourceApplicator<Target, FakeAsset>
        {
            public void Replace(Target target, FakeAsset value) => target.Asset = value;
            public void Revert(Target target) => target.Asset = null;
        }
    }
}
