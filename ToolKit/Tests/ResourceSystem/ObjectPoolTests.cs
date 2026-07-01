/*
 * ObjectPool<T> 测试 (xUnit)
 */

using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem
{
    public class ObjectPoolTests
    {
        private sealed class Item { public int Created; public bool Active; }

        // 测试点：池为空时通过工厂创建新对象。
        [Fact]
        public void Get_WhenEmpty_UsesFactory()
        {
            int created = 0;
            var pool = new ObjectPool<Item>(factory: () => new Item { Created = ++created });
            var a = pool.Get();
            var b = pool.Get();
            Assert.Equal(2, created);
            Assert.NotSame(a, b);
        }

        // 测试点：归还对象后再次获取会复用同一实例。
        [Fact]
        public void Return_ThenGet_ReusesInstance()
        {
            var pool = new ObjectPool<Item>(factory: () => new Item());
            var a = pool.Get();
            pool.Return(a);
            var b = pool.Get();
            Assert.Same(a, b);
        }

        // 测试点：取出和归还对象时会触发对应回调。
        [Fact]
        public void OnGet_OnReturn_Invoked()
        {
            var pool = new ObjectPool<Item>(
                factory: () => new Item(),
                onGet: i => i.Active = true,
                onReturn: i => i.Active = false);

            var a = pool.Get();
            Assert.True(a.Active);
            pool.Return(a);
            Assert.False(a.Active);
        }

        // 测试点：归还超过容量的对象会触发销毁回调。
        [Fact]
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
            Assert.Equal(1, destroyed);
            Assert.Equal(1, pool.Count);
        }

        // 测试点：预热会按指定数量填充对象池。
        [Fact]
        public void Prewarm_FillsPool()
        {
            var pool = new ObjectPool<Item>(factory: () => new Item());
            pool.Prewarm(3);
            Assert.Equal(3, pool.Count);
        }

        // 测试点：清空对象池时会销毁池内所有缓存对象。
        [Fact]
        public void Clear_DestroysAll()
        {
            int destroyed = 0;
            var pool = new ObjectPool<Item>(factory: () => new Item(), onDestroy: _ => destroyed++);
            pool.Prewarm(3);
            pool.Clear();
            Assert.Equal(3, destroyed);
            Assert.Equal(0, pool.Count);
        }

        // 测试点：没有销毁回调时清空对象池也必须正常出队并结束。
        [Fact]
        public void Clear_WithoutDestroy_EmptiesPool()
        {
            var pool = new ObjectPool<Item>(factory: () => new Item());
            pool.Prewarm(3);

            pool.Clear();

            Assert.Equal(0, pool.Count);
        }
    }
}
