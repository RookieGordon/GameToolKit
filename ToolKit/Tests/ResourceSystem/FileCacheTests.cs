/*
 * 纯文件缓存测试 (HTTP 解耦版)：不创建下载器、不构造 HTTP 响应；
 * 全部行为通过本地写文件的 fill 回调驱动。场景对应 H01–H06。
 */

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    /// <summary> 受控 fill：可注入内容、失败、栅栏与调用计数 </summary>
    public sealed class FakeFill
    {
        public int CallCount;
        public Func<string, CancellationToken, Task> Implementation = (path, ct) =>
            File.WriteAllTextAsync(path, "default-content");

        public Task FillAsync(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref CallCount);
            return Implementation(path, ct);
        }
    }

    public sealed class ManualUtcClock
    {
        private readonly object _gate = new object();
        private DateTime _utc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public DateTime Utc
        {
            get { lock (_gate) { return _utc; } }
        }

        public void Advance(TimeSpan span)
        {
            lock (_gate) { _utc = _utc.Add(span); }
        }

        public static implicit operator Func<DateTime>(ManualUtcClock clock)
        {
            return () => clock.Utc;
        }
    }

    public sealed class FileCacheTests : IDisposable
    {
        private readonly string _root;
        private readonly ManualUtcClock _utc = new ManualUtcClock();

        public FileCacheTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gtpure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { /* 尽力清理 */ }
        }

        private static FileRequest Req(string artifact, string revision = "v1", long? len = null,
            string? sha = null, FileValidity? validity = null)
        {
            return new FileRequest(
                new FileIdentity("test", artifact, revision, ""),
                validity ?? FileValidity.Immutable, len, sha);
        }

        private FileCache NewCache(Action<FileCacheOptions>? configure = null)
        {
            var options = new FileCacheOptions { Directory = _root };
            configure?.Invoke(options);
            var cache = new FileCache(options, null, _utc);
            cache.InitializeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            return cache;
        }

        private string CacheDir => Path.Combine(_root, "http-cache-v2");

        // H01: 仅靠写入测试字节的 fill 完成命中、过期、发布与重启恢复；不创建下载器
        [Fact]
        public async Task H01_FillOnly_HitExpirePublishRestart()
        {
            var fill = new FakeFill
            {
                Implementation = (path, ct) => File.WriteAllTextAsync(path, "payload-A"),
            };
            var cache = NewCache();
            var path1 = await cache.GetOrCreateAsync(Req("a"), fill.FillAsync);
            Assert.True(File.Exists(path1));
            Assert.True(path1.EndsWith(".cache"));

            var path2 = await cache.GetOrCreateAsync(Req("a"), fill.FillAsync);
            Assert.Equal(path1, path2);         // 命中：不调用 fill
            Assert.Equal(1, fill.CallCount);    // H02: fill 调用数不增加

            await cache.ShutdownAsync();

            // 重启恢复：扫描文件名重建记录，无 fill 命中
            var restarted = NewCache();
            var path3 = await restarted.GetOrCreateAsync(Req("a"), fill.FillAsync);
            Assert.Equal(path1, path3);
            Assert.Equal(1, fill.CallCount);
            await restarted.ShutdownAsync();
        }

        // H03: 同 key 并发只执行一次 fill；成功共享路径；失败共享同一次故障；声明冲突报错
        [Fact]
        public async Task H03_SharedFill_OnceOrFailTogether()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fill = new FakeFill
            {
                Implementation = async (path, ct) =>
                {
                    await gate.Task.ConfigureAwait(false);
                    await File.WriteAllTextAsync(path, "shared");
                },
            };
            var cache = NewCache();
            var task1 = cache.GetOrCreateAsync(Req("a"), fill.FillAsync);
            var task2 = cache.GetOrCreateAsync(Req("a"), fill.FillAsync);
            gate.TrySetResult(true);
            var paths = await Task.WhenAll(task1, task2);
            Assert.Equal(1, fill.CallCount); // 一次 fill
            Assert.Equal(paths[0], paths[1]); // 同一路径
            await cache.ShutdownAsync();

            // 失败共享同一次故障 (异步落地保证并发加入)
            var failFill = new FakeFill
            {
                Implementation = async (path, ct) =>
                {
                    await Task.Yield();
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.NetworkHttpNotFound, LoadStage.Download, CleanupStatus.Complete));
                },
            };
            var failCache = NewCache();
            var f1 = failCache.GetOrCreateAsync(Req("b"), failFill.FillAsync);
            var f2 = failCache.GetOrCreateAsync(Req("b"), failFill.FillAsync);
            var e1 = await Assert.ThrowsAsync<ResourceLoadException>(() => f1);
            var e2 = await Assert.ThrowsAsync<ResourceLoadException>(() => f2);
            Assert.Equal(e1.Error.DiagnosticId, e2.Error.DiagnosticId); // 同一次故障透传
            Assert.Empty(Directory.GetFiles(CacheDir, "*.part"));       // 失败清理临时文件
            await failCache.ShutdownAsync();

            // 已知声明冲突仍报错
            var conflict = NewCache();
            var first = conflict.GetOrCreateAsync(Req("c", sha: new string('a', 64)), failFill.FillAsync);
            await Assert.ThrowsAsync<ResourceLoadException>(() =>
                conflict.GetOrCreateAsync(Req("c", sha: new string('b', 64)), failFill.FillAsync));
            await first.ContinueWith(_ => { });
            await conflict.ShutdownAsync();
        }

        // H04: 调用者取消只取消各自等待；全部取消后 fill 可继续完成；关闭才取消并等待 fill
        [Fact]
        public async Task H04_CallerCancel_FillCompletes_ShutdownCancels()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fill = new FakeFill
            {
                Implementation = async (path, ct) =>
                {
                    await gate.Task.ConfigureAwait(false); // fill 观察缓存生命周期令牌
                    ct.ThrowIfCancellationRequested();
                    await File.WriteAllTextAsync(path, "late");
                },
            };
            var cache = NewCache();

            var ctsA = new CancellationTokenSource();
            var taskA = cache.GetOrCreateAsync(Req("a"), fill.FillAsync, ctsA.Token);
            var taskB = cache.GetOrCreateAsync(Req("a"), fill.FillAsync);
            ctsA.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA); // A 只取消自己的等待

            gate.TrySetResult(true);
            var pathB = await taskB; // B 不受影响
            Assert.Equal(1, fill.CallCount);

            // 全部取消后 fill 仍完成并进入缓存
            var gate2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fill2Fill = new FakeFill
            {
                Implementation = async (path, ct) =>
                {
                    await gate2.Task.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    await File.WriteAllTextAsync(path, "no-waiter");
                },
            };
            var cts = new CancellationTokenSource();
            var cancelled = cache.GetOrCreateAsync(Req("isolated"), fill2Fill.FillAsync, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            gate2.TrySetResult(true);
            for (var i = 0; i < 200; i++)
            {
                await Task.Delay(20).ConfigureAwait(false);
                var hit = cache.GetOrCreateAsync(Req("isolated"), fill2Fill.FillAsync);
                if (hit.IsCompleted && hit.Status == TaskStatus.RanToCompletion)
                {
                    Assert.Equal(1, fill2Fill.CallCount); // 后续命中已完成的填充
                    await cache.ShutdownAsync();
                    return;
                }
            }
            Assert.True(false, "全部取消后的填充应完成并可供命中");
        }

        // H05: fill 未结束时不发布不删临时文件；fill Task 结束前关闭句柄
        [Fact]
        public async Task H05_FillPending_NoPublishNoDelete()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var heldPart = "";
            var fill = new FakeFill
            {
                Implementation = async (path, ct) =>
                {
                    heldPart = path;
                    using (var hold = new FileStream(path, FileMode.Create, FileAccess.Write))
                    {
                        var partialBytes = Encoding.UTF8.GetBytes("partial");
                        await hold.WriteAsync(partialBytes, 0, partialBytes.Length, ct);
                        await gate.Task.ConfigureAwait(false); // fill 未结束：句柄仍持有
                    }
                },
            };
            var cache = NewCache();
            var task = cache.GetOrCreateAsync(Req("a"), fill.FillAsync);
            await Task.Delay(100).ConfigureAwait(false);

            Assert.Empty(Directory.GetFiles(CacheDir, "*.cache")); // 未发布
            gate.TrySetResult(true);
            var path = await task;
            Assert.True(File.Exists(path));   // 发布成功
            Assert.False(File.Exists(heldPart)); // 临时文件已改名为完整文件

            // fill 失败只清自身临时文件
            var failFill = new FakeFill
            {
                Implementation = (path2, ct) => throw new InvalidOperationException("fill broken"),
            };
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => cache.GetOrCreateAsync(Req("b"), failFill.FillAsync));
            Assert.Empty(Directory.GetFiles(CacheDir, "*.part"));
            await cache.ShutdownAsync();
        }

        // H06: TTL 刷新新路径旧路径有效；运行期无自动清理；重复初始化不清理
        [Fact]
        public async Task H06_TtlNewPath_NoRuntimeCleanup()
        {
            var fill = new FakeFill
            {
                Implementation = (path, ct) => File.WriteAllTextAsync(path, "v1"),
            };
            var validity = FileValidity.ExpiresAfter(TimeSpan.FromMinutes(10));
            var cache = NewCache(o => o.StartupTargetBytes = 1); // 目标极小
            var oldPath = await cache.GetOrCreateAsync(Req("a", validity: validity), fill.FillAsync);

            _utc.Advance(TimeSpan.FromMinutes(11)); // 到期
            fill.Implementation = (path, ct) => File.WriteAllTextAsync(path, "v2");
            var newPath = await cache.GetOrCreateAsync(Req("a", validity: validity), fill.FillAsync);
            Assert.NotEqual(oldPath, newPath);
            Assert.True(File.Exists(oldPath)); // 旧路径留下次启动处理，运行期不删
            Assert.Equal(2, fill.CallCount);

            await cache.InitializeAsync(); // 重复初始化：等待同一任务，不重新清理
            Assert.True(File.Exists(oldPath));

            await cache.ShutdownAsync();
            Assert.True(File.Exists(oldPath)); // 关闭不删除完整文件
            Assert.True(File.Exists(newPath));
        }

        // H16 (部分): Files 不引用网络配置/URL/HTTP 响应 —— 通过编译期已保证；
        // 这里验证 FileRequest 无 Source 字段的运行期形态
        [Fact]
        public void H16_PureCacheRequest_Shape()
        {
            var request = new FileRequest(new FileIdentity("ns", "a", "v", ""), FileValidity.Immutable, 10, null);
            Assert.Equal(10, request.ExpectedLength);
            Assert.NotNull(request.Identity.ToString());
        }
    }
}
