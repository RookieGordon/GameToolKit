/*
 * Review 修复回归测试 —— 磁盘缓存与远端加载器。
 * 覆盖 R01/R06/R10/R11/R12/R13/R14/R15/R16。
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
    public sealed class ReviewFixDiskTests : IDisposable
    {
        private readonly string _root;
        private readonly ManualUtcClock _utc = new ManualUtcClock();
        private readonly ManualClock _mono = new ManualClock();

        public ReviewFixDiskTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gtrev-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { /* 尽力清理 */ }
        }

        private static FileRequest Req(string artifact, string query = "", string revision = "v1",
            long? len = null, string? sha = null, FileValidity? validity = null)
        {
            return new FileRequest(
                new FileIdentity("test", artifact, revision, ""),
                new Uri("https://cdn.example.com/" + artifact + query),
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

        private static async Task WaitUntil(Func<bool> condition, string because)
        {
            for (var i = 0; i < 200 && !condition(); i++)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }
            Assert.True(condition(), because);
        }

        // ---- R01: 默认身份保留查询参数；不同 query 是不同内容 ----
        [Fact]
        public async Task R01a_DefaultIdentity_QueryIsolatesContent()
        {
            var transport = new FakeTransport
            {
                Content = r => Encoding.UTF8.GetBytes("body:" + r.Source.Query),
            };
            var cache = NewCache(transport);
            var manager = new ResourceManager(ImmediateExecutionContext.Instance,
                new ResourceSystemOptions { DefaultLoader = "remote" }, null, null, _mono);
            var loader = new RemoteFileLoader(cache, DecoderRegistry.CreateDefault(),
                defaultValidity: FileValidity.ExpiresAfter(TimeSpan.FromHours(24)));
            manager.RegisterLoader("remote", loader);

            var a = await manager.LoadAsync<byte[]>("https://cdn.example.com/img?id=A");
            var b = await manager.LoadAsync<byte[]>("https://cdn.example.com/img?id=B");

            Assert.Equal("body:?id=A", Encoding.UTF8.GetString(a.Value));
            Assert.Equal("body:?id=B", Encoding.UTF8.GetString(b.Value)); // 不复用 A 的内容
            a.Dispose();
            b.Dispose();
            await manager.ShutdownAsync();
            await cache.ShutdownAsync();
        }

        // ---- R01: 显式 builder 给出稳定身份时，不同签名 URL 合并下载 ----
        [Fact]
        public async Task R01b_BuilderStableIdentity_SignedUrlsShareFile()
        {
            var transport = new FakeTransport();
            var cache = NewCache(transport);

            FileRequest BuildStable(string url)
            {
                var uri = new Uri(url);
                var identity = new FileIdentity("content", uri.AbsolutePath, "v3", "");
                return new FileRequest(identity, uri, null, FileValidity.Immutable);
            }

            var manager = new ResourceManager(ImmediateExecutionContext.Instance,
                new ResourceSystemOptions { DefaultLoader = "remote" }, null, null, _mono);
            manager.RegisterLoader("remote",
                new RemoteFileLoader(cache, DecoderRegistry.CreateDefault(), BuildStable));
            manager.RegisterLoader("remote2",
                new RemoteFileLoader(cache, DecoderRegistry.CreateDefault(), BuildStable));

            var first = await manager.LoadAsync<byte[]>("https://cdn.example.com/data.bin?sig=aaa", loader: "remote");
            var second = await manager.LoadAsync<byte[]>("https://cdn.example.com/data.bin?sig=bbb", loader: "remote2");

            Assert.Equal(1, transport.OpenCount); // 签名不同但内容身份相同：共用一次下载
            first.Dispose();
            second.Dispose();
            await manager.ShutdownAsync();
            await cache.ShutdownAsync();
        }

        // ---- R01: 默认 TTL 到期重新下载，不再永久信任无版本 URL ----
        [Fact]
        public async Task R01c_DefaultTtl_ExpiresAndRedownloads()
        {
            var transport = new FakeTransport();
            var cache = NewCache(transport);
            var loader = new RemoteFileLoader(cache, DecoderRegistry.CreateDefault(),
                defaultValidity: FileValidity.ExpiresAfter(TimeSpan.FromMinutes(10)));
            var manager = new ResourceManager(ImmediateExecutionContext.Instance,
                new ResourceSystemOptions { DefaultLoader = "remote" }, null, null, _mono);
            manager.RegisterLoader("remote", loader);

            var url = "https://cdn.example.com/config.json";
            var first = await manager.LoadAsync<byte[]>(url);
            first.Dispose();
            _utc.Advance(TimeSpan.FromMinutes(5));
            var second = await manager.LoadAsync<byte[]>(url);
            Assert.Equal(1, transport.OpenCount); // TTL 内零网络

            second.Dispose();
            _utc.Advance(TimeSpan.FromMinutes(6)); // 文件 TTL 到期
            _mono.Advance(11);                     // 内存条目按内存策略退出 (X07：文件 TTL 不替换内存对象)
            manager.Tick();                        // 维护回收内存条目后，加载器重新询问文件缓存
            var third = await manager.LoadAsync<byte[]>(url);
            Assert.Equal(2, transport.OpenCount);
            third.Dispose();
            await manager.ShutdownAsync();
            await cache.ShutdownAsync();
        }

        // ---- R06: 解码回退未确认完成时，文件租约进入隔离，容量回收不能删除 ----
        [Fact]
        public async Task R06_UnconfirmedDecodeRollback_FilePinKept()
        {
            var transport = new FakeTransport();
            var cache = NewCache(transport);
            var registry = new DecoderRegistry();
            registry.Register(new RollingBackDecoder());
            var loader = new RemoteFileLoader(cache, registry);
            var manager = new ResourceManager(ImmediateExecutionContext.Instance,
                new ResourceSystemOptions { DefaultLoader = "remote" }, null, null, _mono);
            manager.RegisterLoader("remote", loader);

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(
                () => manager.LoadAsync<byte[]>("https://cdn.example.com/data.bin"));
            Assert.Equal(CleanupStatus.Incomplete, ex.Error.Cleanup);

            var before = cache.GetSnapshot();
            await cache.TrimAsync(0); // 容量回收不能删除被隔离 pin 的文件
            var after = cache.GetSnapshot();
            Assert.Equal(before.AccountedBytes, after.AccountedBytes); // 无可删项：账本不变

            await manager.ShutdownAsync().ContinueWith(_ => { });
        }

        private sealed class RollingBackDecoder : IResourceDecoder, IRepresentationDecoder
        {
            public string Id => "rollback";
            public bool RequiresSourceFile => true; // 中间对象仍依赖源文件

            public bool CanDecode(Type resultType) => resultType == typeof(byte[]);

            public Task<LoadedAsset> DecodeAsync(string path, Type resultType, object? parameters, CancellationToken ct)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetDecodeFailed, LoadStage.Decode, CleanupStatus.Incomplete, null,
                    new Dictionary<string, object> { { "reason", "decoder-leaks-intermediate" } }));
            }
        }

        // ---- R10: 逐块预留口径：总长度明确低于预算的多块文件必须成功 ----
        [Fact]
        public async Task R10_MultiChunkFitsBudget_Succeeds()
        {
            var payload = new byte[131072];
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
            var transport = new FakeTransport { Content = _ => payload };
            var cache = NewCache(transport, o =>
            {
                o.ChunkBytes = 65536;
                o.MetadataAllowanceBytes = 1024;
                o.MaxBytes = 180000; // 实际需求 131072+1024 << 180000，旧口径会虚假 capacity_exceeded
            });

            var lease = await cache.AcquireAsync(Req("a"));
            Assert.Equal(payload, await File.ReadAllBytesAsync(lease.Path));
            var snapshot = cache.GetSnapshot();
            Assert.True(snapshot.AccountedBytes <= snapshot.MaxBytes);
            lease.Dispose();
            await cache.ShutdownAsync();
        }

        // ---- R11: 取消关闭等待不终止真正关闭；锁在核心流程完成后真正释放 ----
        [Fact]
        public async Task R11_CancelledShutdownWait_RealCloseStillReleasesLock()
        {
            var transport = new FakeTransport();
            var cache = NewCache(transport);
            var lease = await cache.AcquireAsync(Req("a")); // 持 pin：关闭无法立即完成

            using var waitCts = new CancellationTokenSource(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ShutdownAsync(waitCts.Token));
            lease.Dispose();
            await cache.ShutdownAsync(); // 等待同一个核心流程完成：锁已真正释放

            var second = new FileCache(new FileCacheOptions { Directory = _root }, null, transport,
                PhysicalFileCacheFileSystem.Instance, _mono, _utc);
            await second.InitializeAsync(); // 不再报 cache.root_in_use
            await second.ShutdownAsync();
        }

        // ---- R12: 下载完成、提交前关闭，等待者必须被结算 ----
        [Fact]
        public async Task R12_ShutdownDuringCommitWaiter_SettledNotHanging()
        {
            FileCache? cacheRef = null;
            var armed = false;
            var transport = new FakeTransport
            {
                ResponseFactory = r =>
                {
                    var bytes = Encoding.UTF8.GetBytes("content-" + r.Identity.ArtifactId);
                    var stream = new MemoryStream(bytes);
                    return new TransportResponse(stream, bytes.Length, 200, null, dispose: () =>
                    {
                        stream.Dispose();
                        if (armed)
                        {
                            // 读循环结束 (提交前) 的窗口内关闭缓存
                            armed = false;
                            _ = cacheRef!.ShutdownAsync();
                        }
                    });
                },
            };
            var cache = NewCache(transport);
            cacheRef = cache;
            armed = true;

            // 无外部取消令牌：若等待者未被结算，本调用将悬挂并使测试超时失败
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => cache.AcquireAsync(Req("a")));
            await WaitUntil(() => cache.GetSnapshot().Jobs == 0, "关闭中的作业应排空");
            Assert.True(ex is ResourceLoadException || ex is OperationCanceledException,
                $"等待者应得到明确终态: {ex.GetType().Name}");
            // 等待者得到明确终态 (系统关闭错误或取消)，而不是永久悬挂
        }

        // ---- R13: 重启后代次不与旧代次路径冲突 ----
        [Fact]
        public async Task R13_GenerationSeedAdvancedAcrossRestart()
        {
            var validity = FileValidity.ExpiresAfter(TimeSpan.FromMinutes(10));
            var transport = new FakeTransport();
            var first = NewCache(transport);
            var lease1 = await first.AcquireAsync(Req("a", validity: validity));
            lease1.Dispose();
            await first.ShutdownAsync();

            _utc.Advance(TimeSpan.FromMinutes(11)); // TTL 过期：重启后须重新下载
            var second = NewCache(transport);
            var lease2 = await second.AcquireAsync(Req("a", validity: validity)); // 不与旧代次路径碰撞
            Assert.Equal(2, transport.OpenCount);
            lease2.Dispose();
            await second.ShutdownAsync();
        }

        // ---- R14: 元数据大小上限：超出 allowance 拒绝提交，不突破总容量 ----
        [Fact]
        public async Task R14_MetadataOverAllowance_CommitRejectedWithinBudget()
        {
            var transport = new FakeTransport
            {
                Content = _ => new byte[8],
            };
            var cache = NewCache(transport, o =>
            {
                o.MaxBytes = 4096;
                o.MetadataAllowanceBytes = 8; // 元数据实际数百字节：必须拒绝提交
            });

            var ex = await Assert.ThrowsAnyAsync<ResourceLoadException>(() => cache.AcquireAsync(Req("a", len: 8)));
            Assert.Equal(DiagnosticCodes.CacheCommitFailed, ex.Error.DiagnosticCode);
            await WaitUntil(() => cache.GetSnapshot().Jobs == 0, "作业应排空");
            var snapshot = cache.GetSnapshot();
            Assert.True(snapshot.AccountedBytes <= snapshot.MaxBytes, $"A={snapshot.AccountedBytes}");
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, "staging")));
            await cache.ShutdownAsync();
        }

        // ---- R15: 元数据临时文件失败后清理并计量，不留未入账残留 ----
        [Fact]
        public async Task R15_MetaRenameFailure_TmpCleanedOrAccounted()
        {
            var transport = new FakeTransport();
            var fs = new FaultInjectingFileSystem
            {
                FailMoveOn = path => path.Contains(".tmp-"),
            };
            var cache = NewCache(transport, fs: fs);

            var ex = await Assert.ThrowsAnyAsync<ResourceLoadException>(() => cache.AcquireAsync(Req("a")));
            Assert.Equal(DiagnosticCodes.CacheCommitFailed, ex.Error.DiagnosticCode);
            await WaitUntil(() => cache.GetSnapshot().Jobs == 0, "作业应排空");

            // 临时元数据不允许静默残留：要么被清理，要么计入账本
            var metaTmpFiles = Directory.GetFiles(Path.Combine(_root, "meta"), "*", SearchOption.AllDirectories)
                .Where(f => f.Contains(".tmp")).ToList();
            var snapshot = cache.GetSnapshot();
            Assert.True(metaTmpFiles.Count == 0 || snapshot.AccountedBytes > 0,
                "残留临时元数据必须计入容量账本");
            await cache.ShutdownAsync();
        }

        // ---- R16: 部分删除失败后重试不重复扣已删除路径的账 ----
        [Fact]
        public async Task R16_PartialDeleteFailure_NoDoubleDeductionOnRetry()
        {
            var transport = new FakeTransport();
            var fs = new FaultInjectingFileSystem();
            var cache = NewCache(transport, fs: fs);
            var lease = await cache.AcquireAsync(Req("a"));
            var dataPath = lease.Path;
            lease.Dispose();
            await WaitUntil(() => cache.GetSnapshot().Jobs == 0, "提交完成");

            // 数据文件允许删除、元数据持续删除失败
            fs.FailDeleteOn = path => !path.Equals(dataPath, StringComparison.OrdinalIgnoreCase);
            var firstTrim = await cache.TrimAsync(0);
            var accountedAfterFirst = cache.GetSnapshot().AccountedBytes;
            Assert.False(firstTrim.TargetReached);
            Assert.True(accountedAfterFirst > 0, "元数据删除失败应保留占用");

            _mono.Advance(6); // 退避过后重试：元数据仍删除失败
            var secondTrim = await cache.TrimAsync(0);
            var accountedAfterSecond = cache.GetSnapshot().AccountedBytes;
            Assert.Equal(accountedAfterFirst, accountedAfterSecond); // 数据文件字节不被重复扣减

            fs.FailDeleteOn = null;
            _mono.Advance(6);
            var finalTrim = await cache.TrimAsync(0);
            Assert.True(finalTrim.TargetReached);
            Assert.Equal(0, cache.GetSnapshot().AccountedBytes);
            await cache.ShutdownAsync();
        }
    }
}
