/*
 * 资源系统 V2 单元测试 —— 解码器与本地/远端加载器 (P5, §8.1/§8.2)。
 */

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class FileLoaderTests : IDisposable
    {
        private readonly string _root;

        public FileLoaderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gtld-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { /* 尽力清理 */ }
        }

        [Fact]
        public async Task LocalFile_TextAndBytes_SeparateEntriesSameSource()
        {
            var file = Path.Combine(_root, "note.txt");
            await File.WriteAllTextAsync(file, "hello toolkit");

            var manager = V2Test.NewManager(out _, out _);
            manager.RegisterLoader("local", new LocalFileLoader(DecoderRegistry.CreateDefault()));

            var text = await manager.LoadAsync<string>(file, loader: "local");
            Assert.Equal("hello toolkit", text.Value);
            var bytes = await manager.LoadAsync<byte[]>(file, loader: "local");
            Assert.Equal("hello toolkit", Encoding.UTF8.GetString(bytes.Value));
            Assert.Equal(2, manager.GetSnapshot().ResourceRows.Count); // 表示不同 → 独立条目

            text.Dispose();
            bytes.Dispose();
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task LocalFile_MissingFile_MapstoFileNotFound()
        {
            var manager = V2Test.NewManager(out _, out _);
            manager.RegisterLoader("local", new LocalFileLoader(DecoderRegistry.CreateDefault()));

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(
                () => manager.LoadAsync<byte[]>(Path.Combine(_root, "absent.bin"), loader: "local"));
            Assert.Equal(DiagnosticCodes.FileNotFound, ex.Error.DiagnosticCode);
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task LocalFile_UnsupportedRepresentation_Rejected()
        {
            var file = Path.Combine(_root, "note.txt");
            await File.WriteAllTextAsync(file, "x");
            var manager = V2Test.NewManager(out _, out _);
            manager.RegisterLoader("local", new LocalFileLoader(DecoderRegistry.CreateDefault()));

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(
                () => manager.LoadAsync<object>(file, loader: "local"));
            Assert.Equal(DiagnosticCodes.AssetUnsupportedRepresentation, ex.Error.DiagnosticCode);
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task RemoteFile_HitCacheAcrossRepresentations()
        {
            var transport = new FakeTransport();
            var cacheOptions = new FileCacheOptions { Directory = Path.Combine(_root, "cache") };
            var cache = new FileCache(cacheOptions, transport);
            await cache.InitializeAsync();

            var manager = V2Test.NewManager(out _, out _);
            manager.RegisterLoader("remote", new RemoteFileLoader(cache, DecoderRegistry.CreateDefault()));

            var url = "https://cdn.example.com/data/config.json";
            var bytes = await manager.LoadAsync<byte[]>(url, loader: "remote");
            Assert.Equal("content-cdn.example.com/data/config.json", Encoding.UTF8.GetString(bytes.Value));
            Assert.Equal(1, transport.OpenCount);

            // 空闲缓存命中 + 文件缓存命中：零网络
            var bytes2 = await manager.LoadAsync<byte[]>(url, loader: "remote");
            Assert.Equal(1, transport.OpenCount);
            Assert.Same(bytes.Value, bytes2.Value);

            // 同一下载文件支撑另一种内存表示：不再下载
            var text = await manager.LoadAsync<string>(url, loader: "remote");
            Assert.Equal(1, transport.OpenCount);
            Assert.Equal("content-cdn.example.com/data/config.json", text.Value);
            Assert.Equal(2, manager.GetSnapshot().ResourceRows.Count); // 文件身份相同但解码表示不同

            text.Dispose();
            bytes.Dispose();
            bytes2.Dispose();
            await manager.ShutdownAsync();
            await cache.ShutdownAsync();
        }

        [Fact]
        public async Task RemoteFile_InvalidUri_RejectedAtResolve()
        {
            var transport = new FakeTransport();
            var cacheOptions = new FileCacheOptions { Directory = Path.Combine(_root, "cache") };
            var cache = new FileCache(cacheOptions, transport);
            await cache.InitializeAsync();

            var manager = V2Test.NewManager(out _, out _);
            manager.RegisterLoader("remote", new RemoteFileLoader(cache, DecoderRegistry.CreateDefault()));

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(
                () => manager.LoadAsync<byte[]>("ftp://example.com/x", loader: "remote"));
            Assert.Equal(DiagnosticCodes.NetworkInvalidUri, ex.Error.DiagnosticCode);
            Assert.Equal(0, transport.OpenCount); // 解析阶段即拒绝，不发起网络
            await manager.ShutdownAsync();
            await cache.ShutdownAsync();
        }
    }
}
