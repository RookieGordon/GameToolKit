/*
 * ResourceBinder 测试: 加载即应用、以最后一次为准、取消保留、解绑、单纯取消应用。
 */

using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ToolKit.Tools.Common;
using static Tests.ResourceSystemTest.AsyncTest;

namespace Tests.ResourceSystemTest
{
    [TestFixture]
    public class ResourceBinderTests
    {
        private static ResourceManager NewMgr(ILoader loader)
        {
            var mgr = new ResourceManager(unloadDelaySeconds: 0);
            mgr.RegisterLoader(loader);
            return mgr;
        }

        [Test]
        public void Apply_Success_AppliesResourceToTarget()
        {
            var mgr = NewMgr(new FakeLoader { Factory = a => new FakeAsset { Name = a } });
            var binder = new ResourceBinder(mgr);
            var app = new CaptureApplicable();
            var target = new FakeTarget();

            Run(binder.ApplyAsync<FakeTarget, FakeAsset>(target, "a", app));

            Assert.AreEqual(1, app.ApplyCount);
            Assert.AreSame(target, app.LastTarget);
            Assert.AreEqual("a", ((FakeAsset)app.LastResource).Name);
            mgr.Dispose();
        }

        [Test]
        public void Apply_SwitchDuringLoad_LastWins()
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
            Assert.IsTrue(Task.WhenAll(tA, tB).Wait(2000));

            Assert.AreEqual(1, app.ApplyCount, "过期请求不应被应用");
            Assert.AreEqual("B", ((FakeAsset)app.LastResource).Name);
            mgr.Dispose();
        }

        [Test]
        public void Apply_Cancelled_KeepsOldResource()
        {
            var mgr = NewMgr(new FakeLoader { Factory = a => new FakeAsset { Name = a } });
            var binder = new ResourceBinder(mgr);
            var app = new CaptureApplicable();
            var target = new FakeTarget();

            Run(binder.ApplyAsync<FakeTarget, FakeAsset>(target, "A", app)); // 应用 A

            var cts = new CancellationTokenSource();
            cts.Cancel();
            Run(binder.ApplyAsync<FakeTarget, FakeAsset>(target, "B", app, ELoadType.Auto, cts.Token));

            Assert.AreEqual(1, app.ApplyCount, "取消的请求不应用");
            Assert.AreEqual("A", ((FakeAsset)app.LastResource).Name, "应保留旧资源");
            mgr.Dispose();
        }

        [Test]
        public void Unbind_ReleasesCurrentResource()
        {
            var mgr = NewMgr(new FakeLoader { Factory = a => new FakeAsset { Name = a } });
            var binder = new ResourceBinder(mgr);
            var target = new FakeTarget();

            Run(binder.ApplyAsync<FakeTarget, FakeAsset>(target, "A", new CaptureApplicable()));
            mgr.TryGetCached("A", out var h);
            Assert.AreEqual(1, h.ReferenceCount);

            binder.Unbind(target);
            Assert.AreEqual(ELoadStatus.Unloaded, h.Status, "解绑应释放并卸载");
            mgr.Dispose();
        }

        [Test]
        public void CancelApply_KeepsCurrentResource()
        {
            var mgr = NewMgr(new FakeLoader { Factory = a => new FakeAsset { Name = a } });
            var binder = new ResourceBinder(mgr);
            var target = new FakeTarget();

            Run(binder.ApplyAsync<FakeTarget, FakeAsset>(target, "A", new CaptureApplicable()));
            mgr.TryGetCached("A", out var h);

            binder.CancelApply(target);

            Assert.AreEqual(1, h.ReferenceCount, "单纯取消应用不应释放当前资源");
            Assert.AreNotEqual(ELoadStatus.Unloaded, h.Status);
            mgr.Dispose();
        }
    }
}
