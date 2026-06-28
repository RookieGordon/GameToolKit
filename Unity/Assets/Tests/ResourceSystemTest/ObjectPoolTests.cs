/*
 * ObjectPool<T> 测试
 */

using NUnit.Framework;
using ToolKit.Tools.Common;

namespace Tests.ResourceSystemTest
{
    [TestFixture]
    public class ObjectPoolTests
    {
        private sealed class Item { public int Created; public bool Active; }

        [Test]
        public void Get_WhenEmpty_UsesFactory()
        {
            int created = 0;
            var pool = new ObjectPool<Item>(factory: () => new Item { Created = ++created });
            var a = pool.Get();
            var b = pool.Get();
            Assert.AreEqual(2, created);
            Assert.AreNotSame(a, b);
        }

        [Test]
        public void Return_ThenGet_ReusesInstance()
        {
            var pool = new ObjectPool<Item>(factory: () => new Item());
            var a = pool.Get();
            pool.Return(a);
            var b = pool.Get();
            Assert.AreSame(a, b, "归还后应复用同一对象");
        }

        [Test]
        public void OnGet_OnReturn_Invoked()
        {
            var pool = new ObjectPool<Item>(
                factory: () => new Item(),
                onGet: i => i.Active = true,
                onReturn: i => i.Active = false);

            var a = pool.Get();
            Assert.IsTrue(a.Active);
            pool.Return(a);
            Assert.IsFalse(a.Active);
        }

        [Test]
        public void Return_OverCapacity_Destroys()
        {
            int destroyed = 0;
            var pool = new ObjectPool<Item>(
                factory: () => new Item(),
                onDestroy: _ => destroyed++,
                capacity: 1);

            var a = pool.Get();
            var b = pool.Get();
            pool.Return(a); // 入池 (count=1)
            pool.Return(b); // 超容量 -> 销毁
            Assert.AreEqual(1, destroyed);
            Assert.AreEqual(1, pool.Count);
        }

        [Test]
        public void Prewarm_FillsPool()
        {
            var pool = new ObjectPool<Item>(factory: () => new Item());
            pool.Prewarm(3);
            Assert.AreEqual(3, pool.Count);
        }

        [Test]
        public void Clear_DestroysAll()
        {
            int destroyed = 0;
            var pool = new ObjectPool<Item>(factory: () => new Item(), onDestroy: _ => destroyed++);
            pool.Prewarm(3);
            pool.Clear();
            Assert.AreEqual(3, destroyed);
            Assert.AreEqual(0, pool.Count);
        }
    }
}
