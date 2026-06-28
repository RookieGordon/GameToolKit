/*
 * AssetHandle 测试: 引用计数、归零回调、卸载。
 */

using NUnit.Framework;
using ToolKit.Tools.Common;

namespace Tests.ResourceSystemTest
{
    [TestFixture]
    public class AssetHandleTests
    {
        [Test]
        public void SetSucceed_ExposesAsset()
        {
            var h = new AssetHandle("a");
            var asset = new FakeAsset { Name = "a" };
            h.SetSucceed(asset, null);

            Assert.IsTrue(h.IsSuccess);
            Assert.AreEqual(ELoadStatus.Succeed, h.Status);
            Assert.AreSame(asset, h.GetAsset<FakeAsset>());
        }

        [Test]
        public void RetainRelease_TracksCount()
        {
            var h = new AssetHandle("a");
            h.SetSucceed(new FakeAsset(), null);

            h.Retain();
            h.Retain();
            Assert.AreEqual(2, h.ReferenceCount);
            h.Release();
            Assert.AreEqual(1, h.ReferenceCount);
        }

        [Test]
        public void OnReachedZero_FiresOnlyAtZero()
        {
            var h = new AssetHandle("a");
            h.SetSucceed(new FakeAsset(), null);
            int zeroHits = 0;
            h.OnReachedZero = _ => zeroHits++;

            h.Retain();
            h.Retain();         // ref=2
            h.Release();        // ref=1, 不触发
            Assert.AreEqual(0, zeroHits);
            h.Release();        // ref=0, 触发一次
            Assert.AreEqual(1, zeroHits);
        }

        [Test]
        public void Unload_RunsUnloadActionOnce_AndMarksUnloaded()
        {
            var h = new AssetHandle("a");
            int unloaded = 0;
            h.SetSucceed(new FakeAsset(), () => unloaded++);

            h.Unload();
            Assert.AreEqual(1, unloaded);
            Assert.AreEqual(ELoadStatus.Unloaded, h.Status);

            h.Unload(); // 幂等
            Assert.AreEqual(1, unloaded);
        }

        [Test]
        public void Release_TooMany_DoesNotThrowOrGoNegative()
        {
            var h = new AssetHandle("a");
            h.SetSucceed(new FakeAsset(), null);
            h.Retain();          // ref=1
            h.Release();         // ref=0
            Assert.DoesNotThrow(() => h.Release()); // 多余释放只报错, 不抛
            Assert.AreEqual(0, h.ReferenceCount);
        }
    }
}
