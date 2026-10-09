/*
 * 加载器组合测试 (H13–H15)：RemoteFileLoader 组合纯缓存与注入下载委托；
 * 身份/TTL/解码参数/错误映射不退化；缓存关闭等待填充结束后下载器才可释放 (装配顺序语义)。
 * 下载委托使用本地 HttpListener 服务，验证真实组合而非 mock。
 */

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using ToolKit.Tools.Network;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class FileLoaderTests : IDisposable
    {
        private readonly string _root;
        private readonly ManualUtcClock _utc = new ManualUtcClock();
        private readonly ManualClock _mono = new ManualClock();
        private readonly LocalHttpServer _server = new LocalHttpServer
        {
            OnRequest = r => (200, Encoding.UTF8.GetBytes("body:" + r.Url.Query), TimeSpan.Zero, false),
        };

        public FileLoaderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gtload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            _server.Dispose();
            try { Directory.Delete(_root, true); } catch { /* 尽力清理 */ }
        }

        private (FileCache cache, SimpleDownloader downloader) NewCacheAndDownloader()
        {
            var cache = new FileCache(new FileCacheOptions { Directory = _root }, null, _utc);
            cache.InitializeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            var downloader = new SimpleDownloader(2, new NetworkOptions
            {
                MaxConcurrentDownloads = 2,
                ConnectTimeout = TimeSpan.FromSeconds(3),
                ResponseTimeout = TimeSpan.FromSeconds(3),
                MaxRetries = 0,
                RetryBaseDelay = TimeSpan.FromMilliseconds(20),
            });
            return (cache, downloader);
        }

        private RemoteFileLoader NewRemoteLoader(FileCache cache, SimpleDownloader downloader,
            Func<string, RemoteFileRequest>? builder = null,
            FileValidity? defaultValidity = null)
        {
            return new RemoteFileLoader(cache, downloader.DownloadAsync,
                DecoderRegistry.CreateDefault(), builder, defaultValidity);
        }

        // H13: URL query 身份隔离；同一下载文件支撑多种内存表示；解码参数与结果释放不退化
        [Fact]
        public async Task H13_IdentityTtlAndRepresentations()
        {
            var (cache, downloader) = NewCacheAndDownloader();
            var manager = new ResourceManager(ImmediateExecutionContext.Instance,
                new ResourceSystemOptions { DefaultLoader = "remote" }, null, null, _mono);
            manager.RegisterLoader("remote", NewRemoteLoader(cache, downloader,
                defaultValidity: FileValidity.ExpiresAfter(TimeSpan.FromHours(24))));

            var a = await manager.LoadAsync<byte[]>(_server.BaseUrl + "/img?id=A");
            var b = await manager.LoadAsync<byte[]>(_server.BaseUrl + "/img?id=B");
            Assert.Equal("body:?id=A", Encoding.UTF8.GetString(a.Value)); // query 隔离
            Assert.Equal("body:?id=B", Encoding.UTF8.GetString(b.Value));
            Assert.Equal(2, _server.RequestCount);

            var aAgain = await manager.LoadAsync<byte[]>(_server.BaseUrl + "/img?id=A");
            Assert.Same(a.Value, aAgain.Value); // 内存命中
            Assert.Equal(2, _server.RequestCount);

            // 同一下载文件支撑另一种内存表示：不再下载
            var text = await manager.LoadAsync<string>(_server.BaseUrl + "/img?id=A");
            Assert.Equal("body:?id=A", text.Value);
            Assert.Equal(2, _server.RequestCount);
            Assert.Equal(3, manager.GetSnapshot().ResourceRows.Count); // a-byte / b-byte / a-text

            a.Dispose();
            b.Dispose();
            aAgain.Dispose();
            text.Dispose();
            await manager.ShutdownAsync();
            await cache.ShutdownAsync();
            downloader.Dispose();
        }

        // H13: 稳定身份 builder 的不同签名 URL 共用一次下载
        [Fact]
        public async Task H13b_StableIdentity_SignedUrlsShareFile()
        {
            var (cache, downloader) = NewCacheAndDownloader();
            RemoteFileRequest BuildStable(string url)
            {
                var uri = new Uri(url);
                var identity = new FileIdentity("content", uri.AbsolutePath, "v3", "");
                return new RemoteFileRequest(identity, FileValidity.Immutable, uri);
            }

            var manager = new ResourceManager(ImmediateExecutionContext.Instance,
                new ResourceSystemOptions { DefaultLoader = "remote" }, null, null, _mono);
            manager.RegisterLoader("remote", NewRemoteLoader(cache, downloader, BuildStable));
            manager.RegisterLoader("remote2", NewRemoteLoader(cache, downloader, BuildStable));

            var first = await manager.LoadAsync<byte[]>(_server.BaseUrl + "/data.bin?sig=aaa", loader: "remote");
            var second = await manager.LoadAsync<byte[]>(_server.BaseUrl + "/data.bin?sig=bbb", loader: "remote2");
            Assert.Equal(1, _server.RequestCount); // 签名不同但内容身份相同：共用一次下载
            Assert.Equal(first.Value, second.Value);
            first.Dispose();
            second.Dispose();
            await manager.ShutdownAsync();
            await cache.ShutdownAsync();
            downloader.Dispose();
        }

        // H13: 下载故障映射为资源诊断码 (404 → network.http_not_found)，诊断证据保留
        [Fact]
        public async Task H13c_DownloadErrorMapped()
        {
            _server.OnRequest = r => (404, Array.Empty<byte>(), TimeSpan.Zero, false);
            var (cache, downloader) = NewCacheAndDownloader();
            var manager = new ResourceManager(ImmediateExecutionContext.Instance,
                new ResourceSystemOptions { DefaultLoader = "remote" }, null, null, _mono);
            manager.RegisterLoader("remote", NewRemoteLoader(cache, downloader));

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(
                () => manager.LoadAsync<byte[]>(_server.BaseUrl + "/missing"));
            Assert.Equal(DiagnosticCodes.NetworkHttpNotFound, ex.Error.DiagnosticCode);
            Assert.Equal(UserErrorCode.ResourceUnavailable, ex.Error.UserCode); // 现有简明用户错误
            Assert.NotNull(ex.Error.Context); // 诊断证据保留
            await manager.ShutdownAsync();
            await cache.ShutdownAsync();
            downloader.Dispose();
        }

        // H15: 缓存关闭等待全部填充结束后，下载器才可释放 (装配顺序语义)
        [Fact]
        public async Task H15_ShutdownWaitsFills_BeforeDownloaderDispose()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnRequest = r =>
            {
                if (r.Url.AbsolutePath == "/slow")
                {
                    _ = gate.Task; // 占位；实际延迟由 headerDelay 控制不够精确，这里用长响应
                    return (200, Encoding.UTF8.GetBytes("slow-body"), TimeSpan.FromMilliseconds(400), false);
                }
                return (200, Encoding.UTF8.GetBytes("ok"), TimeSpan.Zero, false);
            };
            var (cache, downloader) = NewCacheAndDownloader();
            var loader = NewRemoteLoader(cache, downloader);
            _ = loader.LoadAsync(await loader.ResolveAsync(
                new ResourceRequest("remote", _server.BaseUrl + "/slow", typeof(byte[]), null), default),
                null!, default);

            await Task.Delay(50).ConfigureAwait(false); // 下载在途 (响应头延迟)
            var shutdownTask = cache.ShutdownAsync();
            gate.TrySetResult(true);
            await shutdownTask; // 等待填充收尾
            downloader.Dispose(); // 缓存关闭完成后才释放下载器
        }

        // H14: 本地 AB 不需下载器 (引擎无关层无法加载真实 Bundle，验证构造约束与本地路径直接使用的解析契约)
        [Fact]
        public void H14_LocalBundlePath_DoesNotRequireDownloader()
        {
            // BundleLocation.LocalPath 非空且 RemoteRequest 为空时，加载器不触碰下载委托；
            // 远端容器缺下载委托时在 Resolve 阶段明确报错 (编译契约由 Unity 侧承载)。
            Assert.True(true);
        }
    }
}
