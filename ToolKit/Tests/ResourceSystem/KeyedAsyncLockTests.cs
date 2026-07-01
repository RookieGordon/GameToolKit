/*
 * KeyedAsyncLock 测试 (xUnit): 同 key 串行、异 key 并行。纯 async/await。
 */

using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem
{
    public class KeyedAsyncLockTests
    {
        // 测试点：相同 key 的第二个锁请求必须等待第一个释放。
        [Fact]
        public async Task SameKey_SecondWaitsUntilFirstReleased()
        {
            var l = new KeyedAsyncLock();

            var r1 = await l.LockAsync("A");      // 立刻拿到
            var t2 = l.LockAsync("A");            // 同 key, 应阻塞
            Assert.False(t2.IsCompleted, "同 key 第二个应在第一个释放前阻塞");

            r1.Dispose();                          // 释放 -> 唤醒 t2
            var r2 = await t2;                     // 现在应能拿到
            r2.Dispose();
        }

        // 测试点：不同 key 的锁请求互不阻塞。
        [Fact]
        public async Task DifferentKey_DoesNotBlock()
        {
            var l = new KeyedAsyncLock();

            var r1 = await l.LockAsync("A");
            var t2 = l.LockAsync("B");             // 异 key, 不应被 A 阻塞
            Assert.True(t2.IsCompleted, "不同 key 应立刻获得");

            (await t2).Dispose();
            r1.Dispose();
        }

        // 测试点：同一 key 释放后可以再次获取锁。
        [Fact]
        public async Task Reacquire_AfterRelease_Succeeds()
        {
            var l = new KeyedAsyncLock();
            (await l.LockAsync("A")).Dispose();
            var r2 = await l.LockAsync("A");       // 释放后可再次获得
            Assert.NotNull(r2);
            r2.Dispose();
        }
    }
}
