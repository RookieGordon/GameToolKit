/*
 * ResourceManager 测试 (xUnit): 加载去重、引用计数、凭证、重复释放、AcquireRef、延迟卸载、错误模型、泄漏。
 */

using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class ResourceManagerTests : IDisposable
    {
        private readonly FakeLoader _loader;
        private readonly ResourceManager _mgr;

        public ResourceManagerTests()
        {
            _loader = new FakeLoader { Factory = a => new FakeAsset { Name = a } };
            _mgr = new ResourceManager(unloadDelaySeconds: 0); // 立即卸载
            _mgr.RegisterLoader(_loader);
        }

        public void Dispose() => _mgr.Dispose();

        // 测试点：同地址重复加载只执行一次底层加载，并累加引用计数。
        [Fact]
        public async Task Load_SameAddressTwice_LoadsOnce_RefCountTwo()
        {
            var h1 = await _mgr.LoadAssetAsync("a");
            var h2 = await _mgr.LoadAssetAsync("a");

            Assert.Same(h1, h2);
            Assert.Equal(1, _loader.LoadCount);
            Assert.Equal(2, h1.ReferenceCount);

            h1.Release();
            h2.Release();
        }

        // 测试点：LoadRef 返回有效凭证，并能通过凭证取得资源。
        [Fact]
        public async Task LoadRef_GetReturnsAsset()
        {
            using (var r = await _mgr.LoadRefAsync("a"))
            {
                Assert.True(r.IsValid);
                Assert.Equal("a", r.Get<FakeAsset>()!.Name);
                Assert.Equal(1, _mgr.LiveRefCount);
            }
            Assert.Equal(0, _mgr.LiveRefCount);
        }

        // 测试点：立即卸载模式下释放凭证会卸载资源并移出缓存。
        [Fact]
        public async Task ReleaseRef_ImmediateMode_UnloadsAndRemovesFromCache()
        {
            var r = await _mgr.LoadRefAsync("a");
            _mgr.TryGetCached("a", out var h);
            Assert.Equal(1, h!.ReferenceCount);

            r.Dispose();

            Assert.Equal(ELoadStatus.Unloaded, h.Status);
            Assert.False(_mgr.TryGetCached("a", out _));
        }

        // 测试点：AcquireRef 会创建独立凭证并独立维护引用计数。
        [Fact]
        public async Task AcquireRef_GivesIndependentReference()
        {
            var r1 = await _mgr.LoadRefAsync("a");
            _mgr.TryGetCached("a", out var h);
            Assert.Equal(1, h!.ReferenceCount);

            var r2 = r1.AcquireRef();
            Assert.NotNull(r2);
            Assert.Equal(2, h.ReferenceCount);

            r1.Dispose();
            Assert.Equal(1, h.ReferenceCount);
            Assert.NotEqual(ELoadStatus.Unloaded, h.Status);

            r2!.Dispose();
            Assert.Equal(ELoadStatus.Unloaded, h.Status);
        }

        // 测试点：重复释放同一凭证不会重复扣减底层引用计数。
        [Fact]
        public async Task ReleaseRef_Twice_NoDoubleDecrement()
        {
            var r = await _mgr.LoadRefAsync("a");
            _mgr.TryGetCached("a", out var h);

            _mgr.ReleaseRef(r);                 // 正常释放, ref 0
            _mgr.ReleaseRef(r);                 // 重复释放只报错, 不二次扣减
            Assert.Equal(0, h!.ReferenceCount);
        }

        // 测试点：延迟卸载期间资源保留在缓存中，并可被重新激活。
        [Fact]
        public async Task DeferredUnload_KeepsCached_AndRevives()
        {
            var loader = new FakeLoader { Factory = a => new FakeAsset { Name = a } };
            var mgr = new ResourceManager(unloadDelaySeconds: 100); // 长延迟
            mgr.RegisterLoader(loader);

            var r = await mgr.LoadRefAsync("a");
            mgr.TryGetCached("a", out var h);
            r.Dispose();

            Assert.True(mgr.TryGetCached("a", out _));
            Assert.Equal(ELoadStatus.Succeed, h!.Status);

            var r2 = await mgr.LoadRefAsync("a"); // 复活
            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(1, h.ReferenceCount);

            r2.Dispose();
            mgr.Dispose();
        }

        // 测试点：延迟卸载超时后 CollectUnused 会卸载未使用资源。
        [Fact]
        public async Task CollectUnused_AfterDelay_Unloads()
        {
            var loader = new FakeLoader { Factory = a => new FakeAsset { Name = a } };
            var mgr = new ResourceManager(unloadDelaySeconds: 0.05);
            mgr.RegisterLoader(loader);

            var r = await mgr.LoadRefAsync("a");
            r.Dispose();
            Assert.True(mgr.TryGetCached("a", out _));

            await Task.Delay(150);
            mgr.CollectUnused();

            Assert.False(mgr.TryGetCached("a", out _));
            mgr.Dispose();
        }

        // 测试点：加载失败时返回失败凭证并携带错误信息。
        [Fact]
        public async Task LoadRef_Failure_ReturnsFailedRefWithError()
        {
            var mgr = new ResourceManager(unloadDelaySeconds: 0);
            mgr.RegisterLoader(new FailingLoader { Code = ELoadError.NotFound, Msg = "找不到" });

            var r = await mgr.LoadRefAsync("a");

            Assert.NotNull(r);
            Assert.False(r.IsValid);
            Assert.Equal(ELoadError.NotFound, r.Error.Code);
            Assert.Null(r.Get<FakeAsset>());
            r.Dispose(); // 失败凭证 Dispose 是安全空操作
            mgr.Dispose();
        }

        // 测试点：没有可用加载器时抛出带错误码的资源异常。
        [Fact]
        public async Task LoadRef_NoLoader_ThrowsTypedException()
        {
            var mgr = new ResourceManager();
            var ex = await Assert.ThrowsAsync<ResourceException>(() => mgr.LoadRefAsync("nope"));
            Assert.Equal(ELoadError.NoLoader, ex.Code);
            mgr.Dispose();
        }

        // 测试点：管理器销毁时会强制回收未释放凭证并卸载资源。
        [Fact]
        public async Task Dispose_WithOutstandingRef_UnloadsHandle()
        {
            var r = await _mgr.LoadRefAsync("a"); // 故意不释放
            Assert.Equal(1, _mgr.LiveRefCount);
            _mgr.TryGetCached("a", out var h);

            _mgr.Dispose();

            Assert.Equal(ELoadStatus.Unloaded, h!.Status);
        }
    }
}
