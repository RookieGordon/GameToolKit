/*
 * 远端加载器身份测试 (删减版保留部分，对应 S08)：
 * 默认身份保留内容相关 query/端口；显式同身份的不同签名 URL 复用文件；
 * TTL 到期经内存策略退出后重新询问缓存。旧的容量/租约/元数据用例已随删减移除。
 */

using System;
using System.IO;
using System.Text;
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

        private FileCache NewCache(FakeTransport transport)
        {
            var cache = new FileCache(new FileCacheOptions { Directory = _root }, transport, null, null, _utc);
            cache.InitializeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            return cache;
        }

        // S08: 默认身份保留 query —— 不同 query 是不同内容
        [Fact]
        public async Task S08a_DefaultIdentity_QueryIsolatesContent()
        {
            var transport = new FakeTransport
            {
                Content = r => Encoding.UTF8.GetBytes("body:" + r.Source.Query),
            };
            var cache = NewCache(transport);
            var manager = new ResourceManager(ImmediateExecutionContext.Instance,
                new ResourceSystemOptions { DefaultLoader = "remote" }, null, null, _mono);
            manager.RegisterLoader("remote", new RemoteFileLoader(cache, DecoderRegistry.CreateDefault(),
                defaultValidity: FileValidity.ExpiresAfter(TimeSpan.FromHours(24))));

            var a = await manager.LoadAsync<byte[]>("https://cdn.example.com/img?id=A");
            var b = await manager.LoadAsync<byte[]>("https://cdn.example.com/img?id=B");

            Assert.Equal("body:?id=A", Encoding.UTF8.GetString(a.Value));
            Assert.Equal("body:?id=B", Encoding.UTF8.GetString(b.Value)); // 不复用 A 的内容
            Assert.Equal(2, transport.OpenCount);
            a.Dispose();
            b.Dispose();
            await manager.ShutdownAsync();
            await cache.ShutdownAsync();
        }

        // S08: 显式 builder 给出稳定身份时，不同签名 URL 合并下载
        [Fact]
        public async Task S08b_BuilderStableIdentity_SignedUrlsShareFile()
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

        // S09 (加载器层): TTL 到期 + 内存条目退出后重新下载
        [Fact]
        public async Task S09_LoaderLevel_TtlExpiryRedownloads()
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
            _utc.Advance(TimeSpan.FromMinutes(6));  // 文件 TTL 到期
            _mono.Advance(11);                      // 内存条目按内存策略退出 (X07 契约)
            manager.Tick();
            var third = await manager.LoadAsync<byte[]>(url);
            Assert.Equal(2, transport.OpenCount);   // 重新下载到新路径
            third.Dispose();
            await manager.ShutdownAsync();
            await cache.ShutdownAsync();
        }
    }
}
