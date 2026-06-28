/*
 * KeyedAsyncLock 测试: 同 key 串行、异 key 并行。
 */

using NUnit.Framework;
using ToolKit.Tools.Common;

namespace Tests.ResourceSystemTest
{
    [TestFixture]
    public class KeyedAsyncLockTests
    {
        [Test]
        public void SameKey_SecondWaitsUntilFirstReleased()
        {
            var l = new KeyedAsyncLock();

            var r1 = l.LockAsync("A").GetAwaiter().GetResult(); // 立刻拿到

            var t2 = l.LockAsync("A");                          // 同 key, 应阻塞
            Assert.IsFalse(t2.IsCompleted, "同 key 第二个应在第一个释放前阻塞");

            r1.Dispose();                                       // 释放 -> 唤醒 t2
            Assert.IsTrue(t2.Wait(2000), "释放后第二个应在超时内拿到");
            t2.Result.Dispose();
        }

        [Test]
        public void DifferentKey_DoesNotBlock()
        {
            var l = new KeyedAsyncLock();

            var r1 = l.LockAsync("A").GetAwaiter().GetResult();
            var t2 = l.LockAsync("B");                          // 异 key, 不应被 A 阻塞

            Assert.IsTrue(t2.IsCompleted, "不同 key 应立刻获得");
            t2.Result.Dispose();
            r1.Dispose();
        }

        [Test]
        public void Reacquire_AfterRelease_Succeeds()
        {
            var l = new KeyedAsyncLock();
            var r1 = l.LockAsync("A").GetAwaiter().GetResult();
            r1.Dispose();
            var r2 = l.LockAsync("A").GetAwaiter().GetResult(); // 释放后可再次获得
            Assert.IsNotNull(r2);
            r2.Dispose();
        }
    }
}
