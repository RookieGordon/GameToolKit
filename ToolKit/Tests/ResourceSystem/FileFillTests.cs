/*
 * 文件填充行为用例：命中复用、并发只填充一次、失败不发布、启动清理。
 * 只断言业务可观察的契约：路径内容、填充次数、失败后的可重试性。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class FileFillTests : IDisposable
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "resource-fill-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private FileCache NewCache(long? startupTargetBytes = null) =>
            new FileCache(new FileCacheOptions { Directory = _root, StartupTargetBytes = startupTargetBytes });

        private static FileRequest Request(string path, string revision = "1") =>
            new FileRequest(new FileIdentity("test", path, revision, ""));

        [Fact]
        public async Task 首次请求填充并写入内容后续请求直接命中同一路径()
        {
            var cache = NewCache();
            await cache.InitializeAsync();
            var fills = 0;

            var first = await cache.GetOrCreateAsync(Request("a.txt"), (temporary, _) =>
            {
                Interlocked.Increment(ref fills);
                File.WriteAllText(temporary, "hello");
                return Task.CompletedTask;
            }).WaitAsync(TestTimeout);

            var second = await cache.GetOrCreateAsync(Request("a.txt"), (_, _) =>
            {
                Interlocked.Increment(ref fills);
                return Task.CompletedTask;
            }).WaitAsync(TestTimeout);

            Assert.Equal(1, fills);
            Assert.Equal(first, second);
            Assert.Equal("hello", File.ReadAllText(first));
            await cache.DisposeAsync();
        }

        [Fact]
        public async Task 不同版本的同一路径各自填充到不同文件()
        {
            var cache = NewCache();
            await cache.InitializeAsync();

            var first = await cache.GetOrCreateAsync(Request("a.txt", "1"),
                (temporary, _) => { File.WriteAllText(temporary, "v1"); return Task.CompletedTask; })
                .WaitAsync(TestTimeout);
            var second = await cache.GetOrCreateAsync(Request("a.txt", "2"),
                (temporary, _) => { File.WriteAllText(temporary, "v2"); return Task.CompletedTask; })
                .WaitAsync(TestTimeout);

            Assert.NotEqual(first, second);
            Assert.Equal("v1", File.ReadAllText(first));
            Assert.Equal("v2", File.ReadAllText(second));
            await cache.DisposeAsync();
        }

        [Fact]
        public async Task 相同身份的并发请求只填充一次()
        {
            var cache = NewCache();
            await cache.InitializeAsync();

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fills = 0;

            async Task Fill(string temporary, CancellationToken token)
            {
                Interlocked.Increment(ref fills);
                started.TrySetResult(true);
                await gate.Task.ConfigureAwait(false);
                File.WriteAllText(temporary, "shared");
            }

            var first = cache.GetOrCreateAsync(Request("shared.txt"), Fill);
            await started.Task.WaitAsync(TestTimeout);
            var second = cache.GetOrCreateAsync(Request("shared.txt"), Fill);
            gate.TrySetResult(true);

            var paths = await Task.WhenAll(first, second).WaitAsync(TestTimeout);
            Assert.Equal(1, fills);
            Assert.Equal(paths[0], paths[1]);
            await cache.DisposeAsync();
        }

        [Fact]
        public async Task 填充失败不发布最终路径且随后可以重试()
        {
            var cache = NewCache();
            await cache.InitializeAsync();
            var attempts = 0;

            var failed = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                cache.GetOrCreateAsync(Request("bad.txt"), (_, _) =>
                {
                    Interlocked.Increment(ref attempts);
                    throw new IOException("fill broken");
                }).WaitAsync(TestTimeout));
            Assert.Equal(DiagnosticCodes.FileIoFailed, failed.Error.DiagnosticCode);
            Assert.Empty(Directory.GetFiles(_root, "*.bin"));

            var path = await cache.GetOrCreateAsync(Request("bad.txt"), (temporary, _) =>
            {
                Interlocked.Increment(ref attempts);
                File.WriteAllText(temporary, "recovered");
                return Task.CompletedTask;
            }).WaitAsync(TestTimeout);

            Assert.Equal(2, attempts);
            Assert.Equal("recovered", File.ReadAllText(path));
            await cache.DisposeAsync();
        }

        [Fact]
        public async Task 填充未写出文件时按缺少文件失败()
        {
            var cache = NewCache();
            await cache.InitializeAsync();

            var error = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                cache.GetOrCreateAsync(Request("missing.txt"), (_, _) => Task.CompletedTask)
                    .WaitAsync(TestTimeout));

            Assert.Equal(DiagnosticCodes.FileNotFound, error.Error.DiagnosticCode);
            await cache.DisposeAsync();
        }

        [Fact]
        public async Task 初始化清理删除残留临时文件并按目标容量从旧到新删除()
        {
            Directory.CreateDirectory(_root);
            var oldest = Path.Combine(_root, "oldest.bin");
            var middle = Path.Combine(_root, "middle.bin");
            var newest = Path.Combine(_root, "newest.bin");
            var leftover = Path.Combine(_root, "leftover.bin.abcdef.tmp");
            File.WriteAllBytes(oldest, new byte[100]);
            File.WriteAllBytes(middle, new byte[100]);
            File.WriteAllBytes(newest, new byte[100]);
            File.WriteAllBytes(leftover, new byte[10]);
            File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddHours(-3));
            File.SetLastWriteTimeUtc(middle, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(newest, DateTime.UtcNow);

            var cache = NewCache(startupTargetBytes: 150);
            await cache.InitializeAsync();

            Assert.False(File.Exists(leftover));
            Assert.False(File.Exists(oldest));
            Assert.False(File.Exists(middle));
            Assert.True(File.Exists(newest));
            await cache.DisposeAsync();
        }

        [Fact]
        public async Task 过期文件会重新填充()
        {
            var cache = NewCache();
            await cache.InitializeAsync();

            var expired = new FileRequest(new FileIdentity("test", "ttl.txt", "1", ""),
                FileValidity.Until(DateTimeOffset.UtcNow.AddMinutes(-1)));
            var path = await cache.GetOrCreateAsync(expired, (temporary, _) =>
            {
                File.WriteAllText(temporary, "first");
                return Task.CompletedTask;
            }).WaitAsync(TestTimeout);

            Assert.Equal("first", File.ReadAllText(path));

            var again = await cache.GetOrCreateAsync(expired, (temporary, _) =>
            {
                File.WriteAllText(temporary, "second");
                return Task.CompletedTask;
            }).WaitAsync(TestTimeout);

            Assert.Equal(path, again);
            Assert.Equal("second", File.ReadAllText(path));
            await cache.DisposeAsync();
        }

        [Fact]
        public async Task 默认转换表提供字节与文本两种转换()
        {
            var converters = FileResourceConverters.CreateDefault();
            var path = Path.Combine(_root, "sample.txt");
            Directory.CreateDirectory(_root);
            File.WriteAllText(path, "文字内容", new UTF8Encoding(false));

            var bytes = await converters.Get(typeof(byte[]))
                .ConvertAsync(path, typeof(byte[]), null, CancellationToken.None);
            var text = await converters.Get(typeof(string))
                .ConvertAsync(path, typeof(string), null, CancellationToken.None);

            Assert.Equal("文字内容", Assert.IsType<string>(text.Value));
            Assert.Equal(File.ReadAllBytes(path), Assert.IsType<byte[]>(bytes.Value));
            Assert.Throws<ResourceLoadException>(() => converters.Get(typeof(int)));
        }
    }
}
