/*
 * 实例化/对象池 测试 (经 ResourceManager + FakeInstanceProvider)。
 */

using NUnit.Framework;
using ToolKit.Tools.Common;
using static Tests.ResourceSystemTest.AsyncTest;

namespace Tests.ResourceSystemTest
{
    [TestFixture]
    public class InstancePoolTests
    {
        private ResourceManager _mgr;

        private ResourceManager NewMgr(System.Func<string, object> factory)
        {
            var loader = new FakeLoader { Factory = factory };
            var mgr = new ResourceManager(unloadDelaySeconds: 0);
            mgr.RegisterLoader(loader);
            mgr.RegisterInstancer(new FakeInstanceProvider());
            return mgr;
        }

        [TearDown]
        public void TearDown() => _mgr?.Dispose();

        [Test]
        public void Instantiate_ReturnsActiveInstance_CountTracked()
        {
            _mgr = NewMgr(a => new FakePrefab { Name = a });

            var r = Run(_mgr.InstantiateRefAsync("p"));
            var inst = r.Get<FakeInstance>();

            Assert.IsNotNull(inst);
            Assert.IsTrue(inst.Active, "取出时应被激活");
            Assert.AreEqual(1, _mgr.GetLiveInstanceCount("p"));

            r.Dispose();
            Assert.IsFalse(inst.Active, "归还时应失活");
            Assert.AreEqual(0, _mgr.GetLiveInstanceCount("p"));
        }

        [Test]
        public void Recycle_ReusesPooledInstance()
        {
            _mgr = NewMgr(a => new FakePrefab { Name = a });

            var r1 = Run(_mgr.InstantiateRefAsync("p"));
            var inst1 = r1.Get<FakeInstance>();
            r1.Dispose(); // 归还

            var r2 = Run(_mgr.InstantiateRefAsync("p"));
            var inst2 = r2.Get<FakeInstance>();

            Assert.AreSame(inst1, inst2, "应复用池中实例");
            r2.Dispose();
        }

        [Test]
        public void NonInstantiableResource_Throws()
        {
            _mgr = NewMgr(a => new FakeAsset { Name = a }); // 共享资源, 不可实例化

            var ex = Assert.Throws<ResourceException>(
                () => Run(_mgr.InstantiateRefAsync("a")));
            Assert.AreEqual(ELoadError.NotInstantiable, ex.Code);
        }

        [Test]
        public void DestroyedInstance_NotReturnedToPool()
        {
            _mgr = NewMgr(a => new FakePrefab { Name = a });

            var r1 = Run(_mgr.InstantiateRefAsync("p"));
            var inst1 = r1.Get<FakeInstance>();
            inst1.Destroyed = true;       // 模拟引擎销毁
            r1.Dispose();                 // 不应入池
            Assert.AreEqual(0, _mgr.GetLiveInstanceCount("p"));

            var r2 = Run(_mgr.InstantiateRefAsync("p"));
            var inst2 = r2.Get<FakeInstance>();
            Assert.AreNotSame(inst1, inst2, "已销毁实例不应被复用");
            r2.Dispose();
        }
    }
}
