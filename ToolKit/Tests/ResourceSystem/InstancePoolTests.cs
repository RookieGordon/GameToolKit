/*
 * 实例化/对象池 测试 (xUnit, 经 ResourceManager + FakeInstanceProvider)。纯 async/await。
 */

using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class InstancePoolTests : IDisposable
    {
        private ResourceManager? _mgr;

        private ResourceManager NewMgr(Func<string, object> factory)
        {
            var loader = new FakeLoader { Factory = factory };
            var mgr = new ResourceManager(unloadDelaySeconds: 0);
            mgr.RegisterLoader(loader);
            mgr.RegisterInstancer(new FakeInstanceProvider());
            _mgr = mgr;
            return mgr;
        }

        public void Dispose() => _mgr?.Dispose();

        // 测试点：实例化会激活对象，并正确统计在用实例数。
        [Fact]
        public async Task Instantiate_ReturnsActiveInstance_CountTracked()
        {
            var mgr = NewMgr(a => new FakePrefab { Name = a });

            var r = await mgr.InstantiateRefAsync("p");
            var inst = r.Get<FakeInstance>();

            Assert.NotNull(inst);
            Assert.True(inst!.Active, "取出时应被激活");
            Assert.Equal(1, mgr.GetLiveInstanceCount("p"));

            r.Dispose();
            Assert.False(inst.Active, "归还时应失活");
            Assert.Equal(0, mgr.GetLiveInstanceCount("p"));
        }

        // 测试点：释放后的实例会回到池中并在下次实例化时复用。
        [Fact]
        public async Task Recycle_ReusesPooledInstance()
        {
            var mgr = NewMgr(a => new FakePrefab { Name = a });

            var r1 = await mgr.InstantiateRefAsync("p");
            var inst1 = r1.Get<FakeInstance>();
            r1.Dispose(); // 归还

            var r2 = await mgr.InstantiateRefAsync("p");
            var inst2 = r2.Get<FakeInstance>();

            Assert.Same(inst1, inst2);
            r2.Dispose();
        }

        // 测试点：不可实例化的共享资源应抛出明确的资源异常。
        [Fact]
        public async Task NonInstantiableResource_Throws()
        {
            var mgr = NewMgr(a => new FakeAsset { Name = a }); // 共享资源, 不可实例化

            var ex = await Assert.ThrowsAsync<ResourceException>(() => mgr.InstantiateRefAsync("a"));
            Assert.Equal(ELoadError.NotInstantiable, ex.Code);
        }

        // 测试点：已销毁实例释放时不应重新进入对象池。
        [Fact]
        public async Task DestroyedInstance_NotReturnedToPool()
        {
            var mgr = NewMgr(a => new FakePrefab { Name = a });

            var r1 = await mgr.InstantiateRefAsync("p");
            var inst1 = r1.Get<FakeInstance>();
            Assert.NotNull(inst1);
            inst1!.Destroyed = true;      // 模拟引擎销毁
            r1.Dispose();                 // 不应入池
            Assert.Equal(0, mgr.GetLiveInstanceCount("p"));

            var r2 = await mgr.InstantiateRefAsync("p");
            var inst2 = r2.Get<FakeInstance>();
            Assert.NotSame(inst1, inst2); // 已销毁实例不应被复用
            r2.Dispose();
        }
    }
}
