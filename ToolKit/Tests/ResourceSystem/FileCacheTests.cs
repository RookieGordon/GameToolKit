/*
 * 资源系统 V2 单元测试 —— 磁盘缓存 (§14.4 F 系列 / X02)。
 * 真实临时目录 + FakeTransport/FaultInjectingFileSystem；时间用注入时钟精确推进，不用真实 Sleep。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common.Resource;
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

    /// <summary> 注入失败的文件系统装饰器 (F10 磁盘满 / F12 删除失败) </summary>
    public sealed class FaultInjectingFileSystem : IFileCacheFileSystem
    {
        private readonly IFileCacheFileSystem _inner;
        public Func<string, bool>? FailDeleteOn;
        public Func<string, bool>? FailCreateWriteOn;
        public Exception ExceptionToThrow = new IOException("injected io failure");

        public FaultInjectingFileSystem(IFileCacheFileSystem? inner = null)
        {
            _inner = inner ?? PhysicalFileCacheFileSystem.Instance;
        }

        public bool FileExists(string path) => _inner.FileExists(path);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public DateTime GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);

        public Stream CreateWrite(string path)
        {
            if (FailCreateWriteOn != null && FailCreateWriteOn(path))
            {
                throw ExceptionToThrow;
            }
            return _inner.CreateWrite(path);
        }

        public long GetFileLength(string path) => _inner.GetFileLength(path);

        public void DeleteFile(string path)
        {
            if (FailDeleteOn != null && FailDeleteOn(path))
            {
                throw ExceptionToThrow;
            }
            _inner.DeleteFile(path);
        }

        public void MoveFile(string source, string destination, bool overwrite) =>
            _inner.MoveFile(source, destination, overwrite);

        public string[] GetFiles(string directory) => _inner.GetFiles(directory);
        public string[] GetDirectories(string directory) => _inner.GetDirectories(directory);
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
        private readonly ManualClock _mono = new ManualClock();

        public FileCacheTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gtfc-" + Guid.NewGuid().ToString("N"));
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
            IFileCacheFileSystem? fs = null, NetworkOptions? network = null)
        {
            var options = new FileCacheOptions { Directory = _root };
            configure?.Invoke(options);
            var cache = new FileCache(options, network, transport, fs ?? PhysicalFileCacheFileSystem.Instance,
                _mono, _utc);
            cache.InitializeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            return cache;
        }

        private static byte[] BytesOf(string artifact)
        {
            return Encoding.UTF8.GetBytes("content-" + artifact);
        }

        private static async Task WaitUntil(Func<bool> condition, string because)
        {
            for (var i = 0; i < 100 && !condition(); i++)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }
            Assert.True(condition(), because);
        }

        [Fact]
        public async Task F01_LocalHitNoNetwork()
        {
            var transport = new FakeTransport();
            var cache = NewCache(transport);
            var lease1 = await cache.AcquireAsync(Req("a"));
            Assert.Equal(1, transport.OpenCount);
            lease1.Dispose();

            var lease2 = await cache.AcquireAsync(Req("a")); // 有效缓存命中：零网络
            Assert.Equal(1, transport.OpenCount);
            Assert.Equal(BytesOf("a"), await File.ReadAllBytesAsync(lease2.Path));
            var snapshot = cache.GetSnapshot();
            Assert.Single(snapshot.FileRows);
            Assert.Equal(1, snapshot.FileRows[0].Pins); // pin 计数正确
            lease2.Dispose();
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task F02_DownloadMiss_CommitsFileAndMeta()
        {
            var transport = new FakeTransport();
            var cache = NewCache(transport);
            var lease = await cache.AcquireAsync(Req("a", len: BytesOf("a").Length));

            Assert.True(File.Exists(lease.Path));
            Assert.Equal(BytesOf("a"), await File.ReadAllBytesAsync(lease.Path));
            var row = cache.GetSnapshot().FileRows.Single();
            Assert.Equal(FileEntryState.Ready, row.State);
            lease.Dispose();
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task F03_SameFileDownloadOnce_IndependentLeases()
        {
            var transport = new FakeTransport { OpenGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            var cache = NewCache(transport);
            var task1 = cache.AcquireAsync(Req("a"));
            var task2 = cache.AcquireAsync(Req("a"));
            transport.OpenGate.TrySetResult(true);

            var lease1 = await task1;
            var lease2 = await task2;
            Assert.Equal(1, transport.OpenCount); // 只下载一次
            Assert.NotSame(lease1, lease2);       // 各得独立 FileLease
            lease1.Dispose();
            lease2.Dispose();
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task F04_StartupResiduals_NotHitAndCleaned()
        {
            var staging = Path.Combine(_root, "staging");
            Directory.CreateDirectory(staging);
            var strayPart = Path.Combine(staging, "job-x.part");
            await File.WriteAllTextAsync(strayPart, "half-downloaded");

            var transport = new FakeTransport();
            var cache = NewCache(transport);
            var lease = await cache.AcquireAsync(Req("a")); // 遗留临时文件不构成命中
            Assert.Equal(1, transport.OpenCount);
            Assert.Equal(BytesOf("a"), await File.ReadAllBytesAsync(lease.Path));
            lease.Dispose();
            await cache.ShutdownAsync();
            Assert.False(File.Exists(strayPart)); // 启动清理 .part
        }

        [Fact]
        public async Task F05_CapacityLRU_EvictsOldestAccessFirst()
        {
            var transport = new FakeTransport();
            var contentSize = 64 * 1024;
            transport.Content = r => Enumerable.Repeat((byte)'x', contentSize).ToArray();

            var cache = NewCache(transport, o =>
            {
                o.MetadataAllowanceBytes = 2048;
                o.MaxBytes = 3L * contentSize + 3 * 4096 + 8192;
            });

            async Task<string> KeyOfAsync(string artifact)
            {
                var lease = await cache.AcquireAsync(Req(artifact));
                var key = cache.GetSnapshot().FileRows.Single(r => r.Pins == 1).Key;
                lease.Dispose();
                return key;
            }

            var keyA = await KeyOfAsync("a");
            var keyB = await KeyOfAsync("b");
            var keyC = await KeyOfAsync("c");
            var touchB = await cache.AcquireAsync(Req("b"));
            touchB.Dispose(); // b 最近访问

            var leaseD = await cache.AcquireAsync(Req("d")); // 预算不足 → 回收最久未访问的 a
            var snapshot = cache.GetSnapshot();
            Assert.DoesNotContain(snapshot.FileRows, r => r.Key == keyA && r.State == FileEntryState.Ready);
            Assert.Contains(snapshot.FileRows, r => r.Key == keyB && r.State == FileEntryState.Ready);
            Assert.Contains(snapshot.FileRows, r => r.Key == keyC && r.State == FileEntryState.Ready);
            leaseD.Dispose();
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task F06_PinProtectsFile_CapacityErrorWhenOnlyPinned()
        {
            var transport = new FakeTransport();
            transport.Content = r => Enumerable.Repeat((byte)'y', 64 * 1024).ToArray();
            var size = 64 * 1024;
            var cache = NewCache(transport, o =>
            {
                o.MetadataAllowanceBytes = 2048;
                o.MaxBytes = size + 8 * 1024;
            });

            var pinned = await cache.AcquireAsync(Req("a")); // 唯一候选被 pin

            var ex = await Assert.ThrowsAnyAsync<ResourceLoadException>(() => cache.AcquireAsync(Req("b")));
            Assert.Equal(DiagnosticCodes.CacheCapacityExceeded, ex.Error.DiagnosticCode);
            Assert.True(File.Exists(pinned.Path)); // 任何清理操作都不能强删 pin 文件
            pinned.Dispose();
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task F07_UnknownLength_PerChunkBudgetAborts()
        {
            var payload = Enumerable.Repeat((byte)'z', 64 * 1024).ToArray();
            var transport = new FakeTransport
            {
                ResponseFactory = r =>
                {
                    var stream = new MemoryStream(payload);
                    return new TransportResponse(stream, null, 200, null, dispose: () => stream.Dispose());
                },
            };
            var cache = NewCache(transport, o =>
            {
                o.ChunkBytes = 16 * 1024;
                o.MaxBytes = 32 * 1024 + 2 * 1024; // 第二块扩容必然超限
            });

            var ex = await Assert.ThrowsAnyAsync<ResourceLoadException>(() => cache.AcquireAsync(Req("a")));
            Assert.Equal(DiagnosticCodes.CacheCapacityExceeded, ex.Error.DiagnosticCode);

            var snapshot = cache.GetSnapshot();
            Assert.Equal(0, snapshot.ReservedBytes);           // 不遗留预留
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, "staging"))); // 临时占用清理
            Assert.True(snapshot.AccountedBytes <= snapshot.MaxBytes);
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task F09_EntryTooLarge_NetworkBodyNeverStarts()
        {
            var transport = new FakeTransport();
            var cache = NewCache(transport, o => o.MaxBytes = 64 * 1024);

            var ex = await Assert.ThrowsAnyAsync<ResourceLoadException>(
                () => cache.AcquireAsync(Req("a", len: 1024 * 1024)));
            Assert.Equal(DiagnosticCodes.CacheEntryTooLarge, ex.Error.DiagnosticCode);
            Assert.Equal(0, transport.OpenCount); // 网络正文不开始
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task F10_ActualDiskFull_NotReportedAsNetwork()
        {
            var transport = new FakeTransport();
            var diskFull = new IOException("There is not enough space on the disk")
            {
                HResult = unchecked((int)0x80070070),
            };
            var fs = new FaultInjectingFileSystem
            {
                FailCreateWriteOn = path => path.EndsWith(".part"),
                ExceptionToThrow = diskFull,
            };
            var cache = NewCache(transport, fs: fs);

            var ex = await Assert.ThrowsAnyAsync<ResourceLoadException>(() => cache.AcquireAsync(Req("a")));
            Assert.Equal(DiagnosticCodes.CacheDiskFull, ex.Error.DiagnosticCode);
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task F11_IntegrityFailure_NoCommitAndRetryWorks()
        {
            var transport = new FakeTransport
            {
                ResponseFactory = r =>
                {
                    var bytes = BytesOf(r.Identity.ArtifactId);
                    var shortBytes = bytes.Take(bytes.Length - 2).ToArray(); // 正文比声明少 2 字节
                    var stream = new MemoryStream(shortBytes);
                    return new TransportResponse(stream, bytes.Length, 200, null, dispose: () => stream.Dispose());
                },
            };
            var cache = NewCache(transport);

            var ex = await Assert.ThrowsAnyAsync<ResourceLoadException>(() => cache.AcquireAsync(Req("a")));
            Assert.Equal(DiagnosticCodes.CacheIntegrityFailed, ex.Error.DiagnosticCode);
            Assert.Empty(cache.GetSnapshot().FileRows.Where(r => r.State == FileEntryState.Ready));

            transport.ResponseFactory = null; // 服务器修复后重试成功
            var lease = await cache.AcquireAsync(Req("a"));
            Assert.Equal(BytesOf("a"), await File.ReadAllBytesAsync(lease.Path));
            lease.Dispose();
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task F12_DeleteFailure_AccountingKeptAndRetrySucceeds()
        {
            var transport = new FakeTransport();
            var fs = new FaultInjectingFileSystem();
            var cache = NewCache(transport, fs: fs);
            var lease = await cache.AcquireAsync(Req("a"));
            var dataPath = lease.Path;
            lease.Dispose();

            fs.FailDeleteOn = path => path == dataPath; // 文件系统拒绝删除
            var first = await cache.TrimAsync(0);
            Assert.False(first.TargetReached);
            Assert.Contains(first.Errors, e => e.DiagnosticCode == DiagnosticCodes.CacheDeleteFailed);
            Assert.True(File.Exists(dataPath));                       // 占用不下降、不谎称容量充足
            var accounted = cache.GetSnapshot().AccountedBytes;
            Assert.True(accounted >= BytesOf("a").Length - 2);

            fs.FailDeleteOn = null; // 退避期过后在下一次维护中重试删除
            _mono.Advance(6);
            var second = await cache.TrimAsync(0);
            Assert.True(second.TargetReached);
            Assert.False(File.Exists(dataPath));
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task F13_CommitRace_CancelledWaiters_LeaseNeverIssued()
        {
            var transport = new FakeTransport
            {
                OpenGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var cache = NewCache(transport);
            var cts = new CancellationTokenSource();
            var task = cache.AcquireAsync(Req("a"), cts.Token);
            cts.Cancel(); // 提交前取消
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

            transport.OpenGate.TrySetResult(true); // 迟到提交
            await WaitUntil(() => cache.GetSnapshot().Jobs == 0, "迟到提交应排空作业");
            var snapshot = cache.GetSnapshot();
            Assert.Equal(0, snapshot.FileRows.Count(r => r.State == FileEntryState.Ready && r.Pins > 0));

            var lease = await cache.AcquireAsync(Req("a")); // 缓存可用，重新获取
            Assert.Equal(BytesOf("a"), await File.ReadAllBytesAsync(lease.Path));
            lease.Dispose();
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task F14_StartupReconcile_CorruptMetaRebuilt()
        {
            var transport = new FakeTransport();
            var cache = NewCache(transport);
            var lease = await cache.AcquireAsync(Req("a"));
            var dataPath = lease.Path;
            lease.Dispose();
            await cache.ShutdownAsync();

            var metaFile = Directory.GetFiles(Path.Combine(_root, "meta"), "*.meta", SearchOption.AllDirectories)
                .Single();
            await File.WriteAllTextAsync(metaFile, "corrupted-metadata"); // 模拟崩溃/损坏

            var cache2 = NewCache(transport); // 重启：损坏元数据不交付，重建或隔离
            Assert.Equal(1, transport.OpenCount);
            var lease2 = await cache2.AcquireAsync(Req("a"));
            Assert.Equal(2, transport.OpenCount); // 重新下载，不返回不存在路径
            Assert.Equal(BytesOf("a"), await File.ReadAllBytesAsync(lease2.Path));
            lease2.Dispose();
            await cache2.ShutdownAsync();
        }

        [Fact]
        public async Task F16_VersionChange_OldLeaseStaysValid()
        {
            var transport = new FakeTransport();
            var cache = NewCache(transport);
            var v1 = await cache.AcquireAsync(Req("a", revision: "v1"));
            var v1Path = v1.Path;

            var v2 = await cache.AcquireAsync(Req("a", revision: "v2")); // URL 相同但版本身份不同
            Assert.Equal(2, transport.OpenCount);                        // 不误用旧文件
            Assert.NotEqual(v1Path, v2.Path);
            Assert.True(File.Exists(v1Path)); // 旧租约继续有效
            v2.Dispose();
            v1.Dispose();
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task F18_CallerOwnedDownload_SurvivesCacheClear()
        {
            var transport = new FakeTransport();
            var cache = NewCache(transport);
            var destination = Path.Combine(_root, "..", "export-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                await cache.DownloadToAsync(new Uri("https://cdn.example.com/a?sig=t"), destination);
                Assert.True(File.Exists(destination));
                // DownloadToAsync 的内部请求身份固定为 export/direct，内容按 FakeTransport 约定生成
                Assert.Equal(Encoding.UTF8.GetBytes("content-direct"), await File.ReadAllBytesAsync(destination));

                await cache.TrimAsync(0); // 清理资源缓存不删除调用方文件
                Assert.True(File.Exists(destination));
            }
            finally
            {
                if (File.Exists(destination)) File.Delete(destination);
            }
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task X02_QueuedDownloadCancel_TerminalCompletesWithoutSlot()
        {
            var transport = new FakeTransport
            {
                OpenGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var cache = NewCache(transport, network: new NetworkOptions { MaxConcurrentDownloads = 1 });

            var occupying = cache.AcquireAsync(Req("a")); // 占用唯一槽位
            var cts = new CancellationTokenSource();
            var queued = cache.AcquireAsync(Req("b"), cts.Token); // 排队
            cts.Cancel(); // 排队作业全部等待者取消
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

            transport.OpenGate.TrySetResult(true);
            var lease = await occupying;
            lease.Dispose();
            await WaitUntil(() => cache.GetSnapshot().Jobs == 0, "排队作业取消后应无遗留");
            var snapshot = cache.GetSnapshot();
            Assert.Equal(0, snapshot.ReservedBytes); // 不遗留预留
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task Ttl_ExpiresAfter_RedownloadsAtExpiry()
        {
            var transport = new FakeTransport();
            var cache = NewCache(transport);
            var validity = FileValidity.ExpiresAfter(TimeSpan.FromMinutes(10));

            var lease1 = await cache.AcquireAsync(Req("a", validity: validity));
            lease1.Dispose();
            _utc.Advance(TimeSpan.FromMinutes(5));
            var lease2 = await cache.AcquireAsync(Req("a", validity: validity));
            Assert.Equal(1, transport.OpenCount); // TTL 内零网络
            lease2.Dispose();

            _utc.Advance(TimeSpan.FromMinutes(6)); // 到期视为未命中
            var lease3 = await cache.AcquireAsync(Req("a", validity: validity));
            Assert.Equal(2, transport.OpenCount);
            lease3.Dispose();
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task IdentityConflict_ConflictingShaRejected()
        {
            var transport = new FakeTransport
            {
                OpenGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var cache = NewCache(transport);
            var sha1 = new string('a', 64);
            var sha2 = new string('b', 64);

            var cts = new CancellationTokenSource();
            var task1 = cache.AcquireAsync(Req("a", sha: sha1), cts.Token); // 建立作业
            var ex = await Assert.ThrowsAnyAsync<ResourceLoadException>(
                () => cache.AcquireAsync(Req("a", sha: sha2))); // 相同身份、冲突摘要
            Assert.Equal(DiagnosticCodes.CacheIdentityConflict, ex.Error.DiagnosticCode);

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task1);
            transport.OpenGate.TrySetResult(true);
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task RootInUse_SecondInstanceFails()
        {
            var transport = new FakeTransport();
            var cache = NewCache(transport);
            var second = new FileCache(new FileCacheOptions { Directory = _root }, null, transport);
            var ex = await Assert.ThrowsAnyAsync<ResourceLoadException>(() => second.InitializeAsync());
            Assert.Equal(DiagnosticCodes.CacheRootInUse, ex.Error.DiagnosticCode);
            await cache.ShutdownAsync();
        }
    }
}
