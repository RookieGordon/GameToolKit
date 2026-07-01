/*
 * AssetHandle 测试 (xUnit): 引用计数、归零回调、卸载、结构化错误。
 */

using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem
{
    public class AssetHandleTests
    {
        // 测试点：加载成功后句柄状态与资源对象访问。
        [Fact]
        public void SetSucceed_ExposesAsset()
        {
            var h = new AssetHandle("a");
            var asset = new FakeAsset { Name = "a" };
            h.SetSucceed(asset, null);

            Assert.True(h.IsSuccess);
            Assert.Equal(ELoadStatus.Succeed, h.Status);
            Assert.Same(asset, h.GetAsset<FakeAsset>());
        }

        // 测试点：Retain/Release 能正确维护引用计数。
        [Fact]
        public void RetainRelease_TracksCount()
        {
            var h = new AssetHandle("a");
            h.SetSucceed(new FakeAsset(), null);

            h.Retain();
            h.Retain();
            Assert.Equal(2, h.ReferenceCount);
            h.Release();
            Assert.Equal(1, h.ReferenceCount);
        }

        // 测试点：引用计数归零回调只在真正归零时触发。
        [Fact]
        public void OnReachedZero_FiresOnlyAtZero()
        {
            var h = new AssetHandle("a");
            h.SetSucceed(new FakeAsset(), null);
            int zeroHits = 0;
            h.OnReachedZero = _ => zeroHits++;

            h.Retain();
            h.Retain();         // ref=2
            h.Release();        // ref=1, 不触发
            Assert.Equal(0, zeroHits);
            h.Release();        // ref=0, 触发一次
            Assert.Equal(1, zeroHits);
        }

        // 测试点：卸载动作只执行一次，并将句柄标记为已卸载。
        [Fact]
        public void Unload_RunsUnloadActionOnce_AndMarksUnloaded()
        {
            var h = new AssetHandle("a");
            int unloaded = 0;
            h.SetSucceed(new FakeAsset(), () => unloaded++);

            h.Unload();
            Assert.Equal(1, unloaded);
            Assert.Equal(ELoadStatus.Unloaded, h.Status);

            h.Unload(); // 幂等
            Assert.Equal(1, unloaded);
        }

        // 测试点：过量 Release 不抛异常，也不会让引用计数变成负数。
        [Fact]
        public void Release_TooMany_DoesNotThrowOrGoNegative()
        {
            var h = new AssetHandle("a");
            h.SetSucceed(new FakeAsset(), null);
            h.Retain();          // ref=1
            h.Release();         // ref=0
            h.Release();         // 多余释放只报错, 不抛
            Assert.Equal(0, h.ReferenceCount);
        }

        // 测试点：加载失败时能携带结构化错误信息。
        [Fact]
        public void SetFailed_CarriesStructuredError()
        {
            var h = new AssetHandle("a");
            h.SetFailed(ELoadError.NotFound, "找不到 a");

            Assert.False(h.IsSuccess);
            Assert.Equal(ELoadError.NotFound, h.Error.Code);
            Assert.True(h.Error.IsError);
        }
    }
}
