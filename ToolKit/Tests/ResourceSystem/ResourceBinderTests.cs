/*
 * ResourceBinder 测试 (xUnit): 加载即应用、以最后一次为准、取消保留、解绑、单纯取消应用。纯 async/await。
 */

using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem
{
    public class ResourceBinderTests
    {
        private static ResourceManager NewMgr(ILoader loader)
        {
            var mgr = new ResourceManager(unloadDelaySeconds: 0);
            mgr.RegisterLoader(loader);
            return mgr;
        }

        // 测试点：资源加载成功后会应用到目标对象。
        [Fact]
        public async Task Apply_Success_AppliesResourceToTarget()
        {
            var mgr = NewMgr(new FakeLoader { Factory = a => new FakeAsset { Name = a } });
            var binder = new ResourceBinder(mgr);
            var app = new CaptureApplicable();
            var target = new FakeTarget();

            await binder.ApplyAsync<FakeTarget, FakeAsset>(target, "a", app);

            Assert.Equal(1, app.ApplyCount);
            Assert.Same(target, app.LastTarget);
            Assert.Equal("a", ((FakeAsset)app.LastResource!).Name);
            mgr.Dispose();
        }

        // 测试点：加载过程中切换地址时只应用最后一次请求。
        [Fact]
        public async Task Apply_SwitchDuringLoad_LastWins()
        {
            var gated = new GatedLoader { Factory = a => new FakeAsset { Name = a } };
            var mgr = NewMgr(gated);
            var binder = new ResourceBinder(mgr);
            var app = new CaptureApplicable();
            var target = new FakeTarget();

            var tA = binder.ApplyAsync<FakeTarget, FakeAsset>(target, "A", app); // gen=1
            var tB = binder.ApplyAsync<FakeTarget, FakeAsset>(target, "B", app); // gen=2 (更换)

            gated.Release("A");
            gated.Release("B");
            await Task.WhenAll(tA, tB);

            Assert.Equal(1, app.ApplyCount); // 过期请求不应被应用
            Assert.Equal("B", ((FakeAsset)app.LastResource!).Name);
            mgr.Dispose();
        }

        // 测试点：新请求被取消时保留目标上的旧资源。
        [Fact]
        public async Task Apply_Cancelled_KeepsOldResource()
        {
            var mgr = NewMgr(new FakeLoader { Factory = a => new FakeAsset { Name = a } });
            var binder = new ResourceBinder(mgr);
            var app = new CaptureApplicable();
            var target = new FakeTarget();

            await binder.ApplyAsync<FakeTarget, FakeAsset>(target, "A", app); // 应用 A

            var cts = new CancellationTokenSource();
            cts.Cancel();
            await binder.ApplyAsync<FakeTarget, FakeAsset>(target, "B", app, ELoadType.Auto, cts.Token);

            Assert.Equal(1, app.ApplyCount);
            Assert.Equal("A", ((FakeAsset)app.LastResource!).Name); // 保留旧资源
            mgr.Dispose();
        }

        // 测试点：解绑目标时释放当前绑定的资源引用。
        [Fact]
        public async Task Unbind_ReleasesCurrentResource()
        {
            var mgr = NewMgr(new FakeLoader { Factory = a => new FakeAsset { Name = a } });
            var binder = new ResourceBinder(mgr);
            var target = new FakeTarget();

            await binder.ApplyAsync<FakeTarget, FakeAsset>(target, "A", new CaptureApplicable());
            mgr.TryGetCached("A", out var h);
            Assert.Equal(1, h!.ReferenceCount);

            binder.Revert<FakeTarget>(target, null);
            Assert.Equal(ELoadStatus.Unloaded, h.Status);
            mgr.Dispose();
        }

        // 测试点：仅取消挂起应用时不释放当前已绑定资源。
        [Fact]
        public async Task CancelApply_KeepsCurrentResource()
        {
            var mgr = NewMgr(new FakeLoader { Factory = a => new FakeAsset { Name = a } });
            var binder = new ResourceBinder(mgr);
            var target = new FakeTarget();

            await binder.ApplyAsync<FakeTarget, FakeAsset>(target, "A", new CaptureApplicable());
            mgr.TryGetCached("A", out var h);

            binder.CancelApply(target);

            Assert.Equal(1, h!.ReferenceCount); // 单纯取消应用不应释放当前资源
            Assert.NotEqual(ELoadStatus.Unloaded, h.Status);
            mgr.Dispose();
        }
    }
}
