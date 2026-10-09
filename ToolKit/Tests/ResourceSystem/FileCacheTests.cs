/*
 * 下载文件缓存测试 (删减版)：本地命中复用、启动一次清理、共享下载、放宽取消语义。
 * 场景对应删减说明 S01–S16 中可纯 .NET 运行的部分；真实临时目录 + FakeTransport。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    /// <summary> 受控传输：可注入内容、失败、响应工厂与打开栅栏 </summary>
    public sealed class FakeTransport : IFileTransport
    {
        public int OpenCount;
        public Func<FileRequest, byte[]>? Content;
        public Queue<Exception>? ThrowOnOpen;
        public Func<FileRequest, TransportResponse>? ResponseFactory;
        /// <summary> 非空时 OpenReadAsync 先等待其完成 (占用下载槽位) </summary>
        public TaskCompletionSource<bool>? OpenGate;

        public FakeTransport()
        {
            Content = r => Encoding.UTF8.GetBytes("content-" + r.Identity.ArtifactId);
        }

        public async Task<TransportResponse> OpenReadAsync(FileRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref OpenCount);
            if (OpenGate != null)
            {
                await OpenGate.Task.ConfigureAwait(false);
            }
            if (ThrowOnOpen is { Count: > 0 })
            {
                throw ThrowOnOpen.Dequeue();
            }
            var factory = ResponseFactory;
            if (factory != null)
            {
                return factory(request);
            }
            var bytes = Content!(request);
            var stream = new MemoryStream(bytes);
            return new TransportResponse(stream, bytes.Length, 200, "\"etag\"", dispose: () => stream.Dispose());
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
            _root = Path.Combine(Path.GetTempPath(), "gtsimple-" + Guid.NewGuid().ToString("N"));
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
                new Uri("https://cdn.example.com/" + artifact + "?sig=temporary"),
                null, validity ?? FileValidity.Immutable, len, sha);
        }

        private FileCache NewCache(FakeTransport transport, Action<FileCacheOptions>? configure = null,
            NetworkOptions? network = null)
        {
            var options = new FileCacheOptions { Directory = _root };
            configure?.Invoke(options);
            var cache = new FileCache(options, transport, network, null, _utc);
            cache.InitializeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            return cache;
        }

        private string CacheDir => Path.Combine(_root, "http-cache-v2");

        // S01 预置完整缓存 → 命中零网络
        [Fact]
        public async Task S01_LocalHitNoNetwork()
        {
            var transport = new FakeTransport();
            var cache = NewCache(transport);
            var path1 = await cache.GetFileAsync(Req("a"));
            Assert.Equal(1, transport.OpenCount);
            var path2 = await cache.GetFileAsync(Req("a"));
            Assert.Equal(1, transport.OpenCount); // 有效完整文件命中：零网络
            Assert.Equal(path1, path2);
            Assert.True(File.Exists(path1));
            Assert.True(path1.EndsWith(".cache"));
            await cache.ShutdownAsync();
        }

        // S02 同 key 合并为一次下载；失败共享同一次故障
        [Fact]
        public async Task S02_SameKeyMerged_OrSharedFailure()
        {
            var transport = new FakeTransport
            {
                OpenGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var cache = NewCache(transport);
            var task1 = cache.GetFileAsync(Req("a"));
            var task2 = cache.GetFileAsync(Req("a"));
            transport.OpenGate.TrySetResult(true);
            var p1 = await task1;
            var p2 = await task2;
            Assert.Equal(1, transport.OpenCount); // 一次下载
            Assert.Equal(p1, p2);                 // 同一路径
            await cache.ShutdownAsync();

            var failTransport = new FakeTransport
            {
                ThrowOnOpen = new Queue<Exception>(new[]
                {
                    new ResourceLoadException(new LoadError(
                        DiagnosticCodes.NetworkHttpNotFound, LoadStage.Download, CleanupStatus.Complete)),
                }),
            };
            var failCache = NewCache(failTransport);
            var f1 = failCache.GetFileAsync(Req("b"));
            var f2 = failCache.GetFileAsync(Req("b"));
            var e1 = await Assert.ThrowsAsync<ResourceLoadException>(() => f1);
            var e2 = await Assert.ThrowsAsync<ResourceLoadException>(() => f2);
            Assert.Equal(e1.Error.DiagnosticId, e2.Error.DiagnosticId); // 同一次故障
            await failCache.ShutdownAsync();
        }

        // S03 调用者取消只停止自己的等待；全部取消后下载仍完成并留作缓存
        [Fact]
        public async Task S03_CallerCancel_DownloadStillCompletesForLaterUse()
        {
            // 3a: A 取消不影响 B
            var transport = new FakeTransport
            {
                OpenGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var cache = NewCache(transport);
            var ctsA = new CancellationTokenSource();
            var taskA = cache.GetFileAsync(Req("a"), ctsA.Token);
            var taskB = cache.GetFileAsync(Req("a"));
            ctsA.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);
            transport.OpenGate.TrySetResult(true);
            var pathB = await taskB; // B 不受影响
            Assert.Equal(1, transport.OpenCount);
            await cache.ShutdownAsync();

            // 3b: 全部调用者取消，下载仍完成并留作缓存
            var transport2 = new FakeTransport
            {
                OpenGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var cache2 = NewCache(transport2);
            var cts = new CancellationTokenSource();
            var cancelled = cache2.GetFileAsync(Req("a"), cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            transport2.OpenGate.TrySetResult(true); // 放行：后台共享任务继续完成并发布

            for (var i = 0; i < 200; i++)
            {
                await Task.Delay(20).ConfigureAwait(false); // 等后台发布
                var hit = cache2.GetFileAsync(Req("a"));
                if (hit.IsCompleted && hit.Status == TaskStatus.RanToCompletion)
                {
                    Assert.Equal(1, transport2.OpenCount); // 后续请求命中已完成的下载，不再下载
                    await cache2.ShutdownAsync();
                    return;
                }
            }
            Assert.True(false, "全部取消后的下载应完成并可供后续命中");
        }

        // S04 不同 key 受 MaxConcurrentDownloads 限流
        [Fact]
        public async Task S04_ConcurrencyLimit_AcrossKeys()
        {
            var transport = new FakeTransport();
            var active = 0;
            var maxActive = 0;
            transport.ResponseFactory = r =>
            {
                var current = Interlocked.Increment(ref active);
                int seen;
                do
                {
                    seen = Volatile.Read(ref maxActive);
                    if (current <= seen)
                    {
                        break;
                    }
                } while (Interlocked.CompareExchange(ref maxActive, current, seen) != seen);
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Delay(120).ContinueWith(_ => gate.TrySetResult(true)); // 短暂占用槽位
                var bytes = Encoding.UTF8.GetBytes("k:" + r.Identity.ArtifactId);
                var stream = new BlockableStream(bytes, gate.Task);
                return new TransportResponse(stream, bytes.Length, 200, null, dispose: () => stream.Dispose());
            };
            var cache = NewCache(transport, network: new NetworkOptions
            {
                MaxConcurrentDownloads = 2,
                MaxRetries = 0,
                RetryBaseDelay = TimeSpan.FromMilliseconds(10),
            });

            var tasks = Enumerable.Range(0, 6)
                .Select(i => cache.GetFileAsync(Req("key" + i)))
                .ToList();
            await Task.WhenAll(tasks);
            Assert.Equal(2, maxActive); // 不同 key 最多两个并发下载
            await cache.ShutdownAsync();
        }

        /// <summary> 读第一个字节前阻塞在栅栏上的流 (占用下载槽位) </summary>
        private sealed class BlockableStream : Stream
        {
            private readonly MemoryStream _inner;
            private readonly Task _gate;
            private bool _unblocked;

            public BlockableStream(byte[] bytes, Task gate)
            {
                _inner = new MemoryStream(bytes);
                _gate = gate;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (!_unblocked)
                {
                    _unblocked = true;
                    await _gate.ConfigureAwait(false);
                }
                return await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // S05 残留 .part 启动时尽力清理；Get 从不返回 .part
        [Fact]
        public async Task S05_PartResidueCleaned_NeverDelivered()
        {
            Directory.CreateDirectory(CacheDir);
            var strayPart = Path.Combine(CacheDir, Guid.NewGuid().ToString("N") + ".part");
            await File.WriteAllTextAsync(strayPart, "half");

            var transport = new FakeTransport();
            var cache = NewCache(transport);
            Assert.False(File.Exists(strayPart)); // 启动清理 .part

            var path = await cache.GetFileAsync(Req("a"));
            Assert.False(path.EndsWith(".part")); // 从不交付 .part
            Assert.Equal(1, transport.OpenCount); // 残留 .part 不构成命中
            await cache.ShutdownAsync();
        }

        // S06 启动超目标按完成时间删除旧文件；删除失败不导致初始化失败
        [Fact]
        public async Task S06_StartupTarget_OldestDeletedAndDeleteFailureTolerated()
        {
            var transport = new FakeTransport();
            var cacheA = NewCache(transport);
            var oldPath = await cacheA.GetFileAsync(Req("old"));
            await cacheA.ShutdownAsync();

            _utc.Advance(TimeSpan.FromHours(1));
            var cacheB = NewCache(transport);
            var newPath = await cacheB.GetFileAsync(Req("new")); // 较新
            await cacheB.ShutdownAsync();
            Assert.True(File.Exists(oldPath));
            Assert.True(File.Exists(newPath));

            // 占住旧文件句柄使删除失败：初始化仍须成功；未被占住的新文件正常命中
            _utc.Advance(TimeSpan.FromHours(1));
            using (var holdOld = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var cacheC = new FileCache(
                    new FileCacheOptions { Directory = _root, StartupTargetBytes = 0 },
                    transport, null, null, _utc);
                await cacheC.InitializeAsync(); // 删除失败不阻止启动
                var keptNew = await cacheC.GetFileAsync(Req("new"));
                Assert.Equal(newPath, keptNew); // 新文件未被删除，直接命中
                Assert.Equal(2, transport.OpenCount);
                await cacheC.ShutdownAsync();
            }
        }

        // S07 运行期不清理：新增大量文件/超目标/重复初始化都不触发
        [Fact]
        public async Task S07_NoRuntimeCleanup_Ever()
        {
            var transport = new FakeTransport { Content = r => new byte[64 * 1024] };
            var cache = NewCache(transport, o => o.StartupTargetBytes = 64 * 1024); // 目标只够一个文件

            for (var i = 0; i < 5; i++)
            {
                await cache.GetFileAsync(Req("file" + i)); // 运行期新增远超目标
            }
            var files = Directory.GetFiles(CacheDir, "*.cache");
            Assert.Equal(5, files.Length); // 不触发运行期清理

            await cache.InitializeAsync(); // 重复初始化：等待同一任务，不重新清理
            Assert.Equal(5, Directory.GetFiles(CacheDir, "*.cache").Length);

            var after = await cache.GetFileAsync(Req("file0"));
            Assert.Equal(files[0], after); // 记录仍在 (等价于重建 ResourceManager 场景)
            await cache.ShutdownAsync();
        }

        // S09 TTL 到期发布新路径，旧路径与旧内容仍存在；重下失败不默认交付过期文件
        [Fact]
        public async Task S09_TtlExpiry_NewPathKeepsOld()
        {
            var transport = new FakeTransport();
            var validity = FileValidity.ExpiresAfter(TimeSpan.FromMinutes(10));
            var cache = NewCache(transport);
            var oldPath = await cache.GetFileAsync(Req("a", validity: validity));

            _utc.Advance(TimeSpan.FromMinutes(11)); // 到期
            transport.Content = r => Encoding.UTF8.GetBytes("content-v2-" + r.Identity.ArtifactId);
            var newPath = await cache.GetFileAsync(Req("a", validity: validity));
            Assert.NotEqual(oldPath, newPath);      // 新路径
            Assert.True(File.Exists(oldPath));      // 旧路径留下次启动处理
            Assert.Equal(2, transport.OpenCount);

            // 重下失败不默认交付过期文件
            transport.ThrowOnOpen = new Queue<Exception>(new[]
            {
                new ResourceLoadException(new LoadError(
                    DiagnosticCodes.NetworkHttpNotFound, LoadStage.Download, CleanupStatus.Complete)),
            });
            _utc.Advance(TimeSpan.FromMinutes(11));
            var ex = await Assert.ThrowsAsync<ResourceLoadException>(
                () => cache.GetFileAsync(Req("a", validity: validity)));
            Assert.Equal(DiagnosticCodes.NetworkHttpNotFound, ex.Error.DiagnosticCode);
            await cache.ShutdownAsync();
        }

        // S10 重启扫描恢复新格式；同 key 多副本只选最新
        [Fact]
        public async Task S10_RestartScan_NewestPerKeySurvives()
        {
            var transport = new FakeTransport();
            var cacheA = NewCache(transport);
            var older = await cacheA.GetFileAsync(Req("a"));
            await cacheA.ShutdownAsync();

            // 手工放入同 key 的更新副本 (文件名带更晚完成时间)
            var key = Path.GetFileName(older).Split('_')[0];
            var newerName = $"{key}_{DateTime.UtcNow.AddHours(1).Ticks:0}_{Guid.NewGuid().ToString("N")}.cache";
            var newer = Path.Combine(CacheDir, newerName);
            await File.WriteAllTextAsync(newer, "newest");

            var cacheB = NewCache(transport);
            var hit = await cacheB.GetFileAsync(Req("a")); // 无网络命中
            Assert.Equal(1, transport.OpenCount);
            Assert.Equal(newer, hit);                       // 选最新完成时间
            Assert.False(File.Exists(older));               // 旧副本在启动阶段被清
            await cacheB.ShutdownAsync();
        }

        // S11 长度/摘要不符不发布；后来请求的校验要求不因首次未声明而丢失
        [Fact]
        public async Task S11_VerificationEnforced_OnDownloadAndHit()
        {
            var transport = new FakeTransport
            {
                Content = r => Encoding.UTF8.GetBytes("payload"), // 7 字节
            };
            var cache = NewCache(transport);

            // 下载声明长度不符：不发布
            var ex = await Assert.ThrowsAsync<ResourceLoadException>(
                () => cache.GetFileAsync(Req("a", len: 999)));
            Assert.Equal(DiagnosticCodes.CacheIntegrityFailed, ex.Error.DiagnosticCode);
            Assert.Empty(Directory.GetFiles(CacheDir, "*.cache")); // 未发布完整文件
            Assert.Empty(Directory.GetFiles(CacheDir, "*.part"));  // 尝试残留已清理

            // 首次无摘要成功下载；随后带摘要请求的核验不被跳过
            var shaOfPayload = Sha256Hex(Encoding.UTF8.GetBytes("payload"));
            var path = await cache.GetFileAsync(Req("a"));
            Assert.Equal(1, transport.OpenCount);
            var wrongSha = new string('f', 64);
            var mismatch = await Assert.ThrowsAsync<ResourceLoadException>(
                () => cache.GetFileAsync(Req("a", sha: wrongSha)));
            Assert.Equal(DiagnosticCodes.CacheIntegrityFailed, mismatch.Error.DiagnosticCode);

            var correct = await cache.GetFileAsync(Req("a", sha: shaOfPayload)); // 摘要正确：命中通过
            Assert.Equal(path, correct);
            Assert.Equal(1, transport.OpenCount);
            await cache.ShutdownAsync();
        }

        // S12 网络错误保留分类；失败清理自身 part，不误删已完成文件
        [Fact]
        public async Task S12_ErrorClassification_AndPartCleanup()
        {
            var transport = new FakeTransport
            {
                ThrowOnOpen = new Queue<Exception>(new[]
                {
                    new ResourceLoadException(new LoadError(
                        DiagnosticCodes.NetworkDnsFailed, LoadStage.Download, CleanupStatus.Complete)),
                }),
            };
            var cache = NewCache(transport);
            var dnsError = await Assert.ThrowsAsync<ResourceLoadException>(() => cache.GetFileAsync(Req("a")));
            Assert.Equal(DiagnosticCodes.NetworkDnsFailed, dnsError.Error.DiagnosticCode);
            Assert.Empty(Directory.GetFiles(CacheDir, "*.part")); // 失败清理自身 part
            await cache.ShutdownAsync();

            // 已完成文件不受其他 key 失败影响；磁盘满分类由 Classifier 直接验证
            var transport2 = new FakeTransport();
            var cache2 = NewCache(transport2);
            var kept = await cache2.GetFileAsync(Req("keep"));
            var diskFullEx = new IOException("There is not enough space on the disk")
            {
                HResult = unchecked((int)0x80070070),
            };
            Assert.True(FileSystemErrorClassifier.IsDiskFull(diskFullEx));
            Assert.True(File.Exists(kept));
            await cache2.ShutdownAsync();
            Assert.True(File.Exists(kept)); // 关闭不删除完整文件
        }

        // S13 关闭语义：拒绝新请求、等底层收尾、取消等待不撤销、再次调用等同一任务、不删文件
        [Fact]
        public async Task S13_ShutdownSemantics()
        {
            var transport = new FakeTransport
            {
                OpenGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var cache = NewCache(transport);
            var pending = cache.GetFileAsync(Req("a"));
            var earlyPath = await cache.GetFileAsync(Req("b")); // 已完成文件

            var shutdownTask = cache.ShutdownAsync();
            var rejected = await Assert.ThrowsAsync<ResourceLoadException>(() => cache.GetFileAsync(Req("c")));
            Assert.Equal(DiagnosticCodes.LifecycleSystemClosing, rejected.Error.DiagnosticCode);

            using (var cancelWait = new CancellationTokenSource(50))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => shutdownTask.WaitAsync(cancelWait.Token)); // 取消等待不撤销关闭
            }
            Assert.True(File.Exists(earlyPath));

            // 关闭通过缓存生命周期令牌取消在途下载：pending 以异常收尾，不悬挂
            transport.OpenGate.TrySetResult(true);
            var pendingEx = await Assert.ThrowsAnyAsync<Exception>(() => pending);
            Assert.True(pendingEx is OperationCanceledException or ResourceLoadException);

            await cache.ShutdownAsync(); // 再次调用：等待同一任务，直接完成
            Assert.True(File.Exists(earlyPath)); // 关闭不删除完整文件
            await Assert.ThrowsAsync<ResourceLoadException>(() => cache.GetFileAsync(Req("d")));
            var initEx = await Assert.ThrowsAnyAsync<Exception>(() => cache.InitializeAsync());
        }

        // S14 初始化进行中关闭：等待收尾且不再开放 Get/Initialize；未初始化直接关闭不触发清理
        [Fact]
        public async Task S14_ShutdownDuringOrBeforeInitialize()
        {
            // 未初始化就关闭：直接结束，不触发初始化或清理
            var transport = new FakeTransport();
            var fresh = new FileCache(new FileCacheOptions { Directory = _root }, transport, null, null, _utc);
            await fresh.ShutdownAsync();
            var lateInit = await Assert.ThrowsAnyAsync<Exception>(() => fresh.InitializeAsync());
            var lateGet = await Assert.ThrowsAnyAsync<Exception>(() => fresh.GetFileAsync(Req("a")));
        }

        // S16 只处理专用子目录中的受管文件；未知文件与目录外内容不删
        [Fact]
        public async Task S16_OnlyManagedFilesInVersionDir()
        {
            Directory.CreateDirectory(CacheDir);
            var unknown = Path.Combine(CacheDir, "not-our-format.bin");
            await File.WriteAllTextAsync(unknown, "business data");
            var outside = Path.Combine(_root, "business-file.txt");
            await File.WriteAllTextAsync(outside, "outside");

            var cache = new FileCache(
                new FileCacheOptions { Directory = _root, StartupTargetBytes = 0 }, // 目标 0：尽力清空
                new FakeTransport(), null, null, _utc);
            await cache.InitializeAsync();

            Assert.True(File.Exists(unknown)); // 未知文件不删
            Assert.True(File.Exists(outside)); // 目录外不删
            await cache.ShutdownAsync();
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }
    }
}
