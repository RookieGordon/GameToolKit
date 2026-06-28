/*
 * ResourceManager 测试: 加载去重、引用计数、凭证、重复释放、AcquireRef、延迟卸载、泄漏。
 */

using System.Threading;
using NUnit.Framework;
using ToolKit.Tools.Common;
using static Tests.ResourceSystemTest.AsyncTest;

namespace Tests.ResourceSystemTest
{
    [TestFixture]
    public class ResourceManagerTests
    {
        private FakeLoader _loader;
        private ResourceManager _mgr;

        [SetUp]
        public void SetUp()
        {
            _loader = new FakeLoader { Factory = a => new FakeAsset { Name = a } };
            _mgr = new ResourceManager(unloadDelaySeconds: 0); // 立即卸载
            _mgr.RegisterLoader(_loader);
        }

        [TearDown]
        public void TearDown() => _mgr.Dispose();

        [Test]
        public void Load_SameAddressTwice_LoadsOnce_RefCountTwo()
        {
            var h1 = Run(_mgr.LoadAssetAsync("a"));
            var h2 = Run(_mgr.LoadAssetAsync("a"));

            Assert.AreSame(h1, h2, "同地址应复用同一句柄");
            Assert.AreEqual(1, _loader.LoadCount, "只应真实加载一次");
            Assert.AreEqual(2, h1.ReferenceCount);

            h1.Release();
            h2.Release();
        }

        [Test]
        public void LoadRef_GetReturnsAsset()
        {
            using (var r = Run(_mgr.LoadRefAsync("a")))
            {
                Assert.IsTrue(r.IsValid);
                Assert.AreEqual("a", r.Get<FakeAsset>().Name);
                Assert.AreEqual(1, _mgr.LiveRefCount);
            }
            Assert.AreEqual(0, _mgr.LiveRefCount, "Dispose 后凭证应清零");
        }

        [Test]
        public void ReleaseRef_ImmediateMode_UnloadsAndRemovesFromCache()
        {
            var r = Run(_mgr.LoadRefAsync("a"));
            _mgr.TryGetCached("a", out var h);
            Assert.AreEqual(1, h.ReferenceCount);

            r.Dispose();

            Assert.AreEqual(ELoadStatus.Unloaded, h.Status);
            Assert.IsFalse(_mgr.TryGetCached("a", out _), "立即卸载后应移出缓存");
        }

        [Test]
        public void AcquireRef_GivesIndependentReference()
        {
            var r1 = Run(_mgr.LoadRefAsync("a"));
            _mgr.TryGetCached("a", out var h);
            Assert.AreEqual(1, h.ReferenceCount);

            var r2 = r1.AcquireRef();
            Assert.IsNotNull(r2);
            Assert.AreEqual(2, h.ReferenceCount);

            r1.Dispose();
            Assert.AreEqual(1, h.ReferenceCount, "释放一份后资源不应卸载");
            Assert.AreNotEqual(ELoadStatus.Unloaded, h.Status);

            r2.Dispose();
            Assert.AreEqual(ELoadStatus.Unloaded, h.Status, "最后一份释放后才卸载");
        }

        [Test]
        public void ReleaseRef_Twice_NoDoubleDecrement()
        {
            var r = Run(_mgr.LoadRefAsync("a"));
            _mgr.TryGetCached("a", out var h);

            _mgr.ReleaseRef(r);                 // 正常释放, ref 0
            Assert.DoesNotThrow(() => _mgr.ReleaseRef(r)); // 重复释放只报错
            Assert.AreEqual(0, h.ReferenceCount, "不应被二次扣减到负数");
        }

        [Test]
        public void DeferredUnload_KeepsCached_AndRevives()
        {
            var loader = new FakeLoader { Factory = a => new FakeAsset { Name = a } };
            var mgr = new ResourceManager(unloadDelaySeconds: 100); // 长延迟
            mgr.RegisterLoader(loader);

            var r = Run(mgr.LoadRefAsync("a"));
            mgr.TryGetCached("a", out var h);
            r.Dispose();

            Assert.IsTrue(mgr.TryGetCached("a", out _), "延迟卸载期间应仍在缓存");
            Assert.AreEqual(ELoadStatus.Succeed, h.Status);

            var r2 = Run(mgr.LoadRefAsync("a")); // 复活
            Assert.AreEqual(1, loader.LoadCount, "复活不应重新加载");
            Assert.AreEqual(1, h.ReferenceCount);

            r2.Dispose();
            mgr.Dispose();
        }

        [Test]
        public void CollectUnused_AfterDelay_Unloads()
        {
            var loader = new FakeLoader { Factory = a => new FakeAsset { Name = a } };
            var mgr = new ResourceManager(unloadDelaySeconds: 0.05);
            mgr.RegisterLoader(loader);

            var r = Run(mgr.LoadRefAsync("a"));
            r.Dispose();
            Assert.IsTrue(mgr.TryGetCached("a", out _), "刚释放应仍在(延迟未到)");

            Thread.Sleep(150);
            mgr.CollectUnused();

            Assert.IsFalse(mgr.TryGetCached("a", out _), "超过延迟后 CollectUnused 应卸载");
            mgr.Dispose();
        }

        [Test]
        public void LoadRef_Failure_ReturnsFailedRefWithError()
        {
            var mgr = new ResourceManager(unloadDelaySeconds: 0);
            mgr.RegisterLoader(new FailingLoader { Code = ELoadError.NotFound, Msg = "找不到" });

            var r = Run(mgr.LoadRefAsync("a"));

            Assert.IsNotNull(r, "失败也应返回凭证, 而非 null");
            Assert.IsFalse(r.IsValid);
            Assert.AreEqual(ELoadError.NotFound, r.Error.Code);
            Assert.IsNull(r.Get<FakeAsset>());
            Assert.DoesNotThrow(() => r.Dispose()); // 失败凭证 Dispose 是安全空操作
            mgr.Dispose();
        }

        [Test]
        public void LoadRef_NoLoader_ThrowsTypedException()
        {
            var mgr = new ResourceManager();
            var ex = Assert.Throws<ResourceException>(() => Run(mgr.LoadRefAsync("nope")));
            Assert.AreEqual(ELoadError.NoLoader, ex.Code);
            mgr.Dispose();
        }

        [Test]
        public void Dispose_WithOutstandingRef_UnloadsHandle()
        {
            var r = Run(_mgr.LoadRefAsync("a")); // 故意不释放
            Assert.AreEqual(1, _mgr.LiveRefCount);
            _mgr.TryGetCached("a", out var h);

            _mgr.Dispose(); // 含泄漏检测 + 卸载全部

            Assert.AreEqual(ELoadStatus.Unloaded, h.Status);
        }
    }
}
