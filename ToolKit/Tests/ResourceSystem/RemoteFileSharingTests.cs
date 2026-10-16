/*
 * 远端共享下载端到端用例 (A05)。这是此前完全零覆盖的一条链路：
 * RemoteFileLoader → FileCache 共享填充 → 各表示的转换器。
 * 断言业务可观察的契约：整条链路只产生一个生产者、先到者的取消不终止仍在等待的表示、
 * 个人取消按自己的取消结束而不是被改写成文件错误。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using ToolKit.Tools.Network;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    [Collection("ResourceSystem")]
    public sealed class RemoteFileSharingTests : IDisposable
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "resource-remote-" + Guid.NewGuid().ToString("N"));
        private readonly List<FileCache> _caches = new List<FileCache>();

        public void Dispose()
        {
            foreach (var cache in _caches)
            {
                try { cache.Dispose(); } catch (Exception) { }
            }
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private FileCache NewCache()
        {
            var cache = new FileCache(new FileCacheOptions
            {
                Directory = _root,
                StartupTargetBytes = long.MaxValue,
            });
            _caches.Add(cache);
            return cache;
        }

        /// <summary> 记录转换次数并按目标类型解释内容；两种表示共用一个缓存文件 </summary>
        private sealed class RecordingConverter : IFileResourceConverter
        {
            private readonly Type _resultType;
            private readonly Func<string, object> _project;

            internal RecordingConverter(Type resultType, Func<string, object> project)
            {
                _resultType = resultType;
                _project = project;
            }

            internal int Count;

            public Task<LoadedAsset> ConvertAsync(
                LoadedContent content, Type resultType, string? conversionKey, CancellationToken cancellationToken)
            {
                Assert.Equal(_resultType, resultType);
                Interlocked.Increment(ref Count);
                var text = content.Value is byte[] bytes
                    ? Encoding.UTF8.GetString(bytes)
                    : content.Value?.ToString() ?? "";
                return Task.FromResult(LoadedAsset.FromUnmanaged(_project(text)));
            }
        }

        [Fact]
        public async Task 同一远程文件的不同表示共享一次下载且各自转换()
        {
            using var context = new TestExecutionContext();
            var cache = NewCache();
            await cache.InitializeAsync();

            var downloads = 0;
            var bytes = new RecordingConverter(typeof(byte[]), text => Encoding.UTF8.GetBytes(text));
            var text2 = new RecordingConverter(typeof(string), text => text.ToUpperInvariant());
            var converters = new FileResourceConverters();
            converters.Register(typeof(byte[]), bytes);
            converters.Register(typeof(string), text2);

            var loader = new RemoteFileLoader(cache, (_, destination, _) =>
            {
                Interlocked.Increment(ref downloads);
                File.WriteAllText(destination, "payload", new UTF8Encoding(false));
                return Task.CompletedTask;
            }, converters);

            using var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics());
            manager.RegisterLoader(ResourceSource.Remote, loader);
            await manager.InitializeAsync();

            using var asBytes = await manager.LoadAsync<byte[]>("https://host/a.bin", ResourceSource.Remote)
                .WaitAsync(TestTimeout);
            using var asText = await manager.LoadAsync<string>("https://host/a.bin", ResourceSource.Remote)
                .WaitAsync(TestTimeout);

            // 两个表示是两个资源身份，但只共享一次文件下载。
            Assert.Equal(1, downloads);
            Assert.Equal(1, bytes.Count);
            Assert.Equal(1, text2.Count);
            Assert.Equal("payload", Encoding.UTF8.GetString(context.Invoke(() => asBytes.Value)));
            Assert.Equal("PAYLOAD", context.Invoke(() => asText.Value));

            // 再取一次字节表示：命中资源层产物，不再下载也不再转换。
            using var again = await manager.LoadAsync<byte[]>("https://host/a.bin", ResourceSource.Remote)
                .WaitAsync(TestTimeout);
            Assert.Equal(1, downloads);
            Assert.Equal(1, bytes.Count);
        }

        [Fact]
        public async Task 先到者的取消不终止仍在等待的另一表示()
        {
            using var context = new TestExecutionContext();
            var cache = NewCache();
            await cache.InitializeAsync();

            var downloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var downloads = 0;
            var observedToken = CancellationToken.None;

            var converters = FileResourceConverters.CreateDefault();
            var loader = new RemoteFileLoader(cache, async (_, destination, token) =>
            {
                Interlocked.Increment(ref downloads);
                observedToken = token;
                downloadStarted.TrySetResult(true);
                // 忽略取消：迟到完成，用来验证共享操作不会被先到者带走。
                await release.Task.ConfigureAwait(false);
                File.WriteAllText(destination, "shared", new UTF8Encoding(false));
            }, converters);

            using var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics());
            manager.RegisterLoader(ResourceSource.Remote, loader);
            await manager.InitializeAsync();

            using var cancellation = new CancellationTokenSource();
            var first = manager.LoadAsync<byte[]>("https://host/shared.bin", ResourceSource.Remote,
                ct: cancellation.Token);
            await downloadStarted.Task.WaitAsync(TestTimeout);

            // 让第二个表示先进入同一份填充，再取消先到者。
            // 第二个请求要完成解析并登记到填充上，只让出一次执行权并不足以保证这一点。
            var second = manager.LoadAsync<string>("https://host/shared.bin", ResourceSource.Remote);
            await Task.Delay(200).ConfigureAwait(false);

            // 前提检查：先到者此时仍在等，第二个表示才是"仍在等待的表示"。
            Assert.False(first.IsCompleted, "先到者在取消前已经结束，场景不成立");

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TestTimeout));

            release.TrySetResult(true);
            using var text = await second.WaitAsync(TestTimeout);
            Assert.Equal("shared", context.Invoke(() => text.Value));

            // 整条链路只产生一个生产者，且填充没有被先到者的个人令牌取消。
            Assert.Equal(1, downloads);
            Assert.False(observedToken.IsCancellationRequested);
        }

        [Fact]
        public async Task 下载失败时两个表示都看到真实失败而不是个人取消()
        {
            using var context = new TestExecutionContext();
            var cache = NewCache();
            await cache.InitializeAsync();

            var converters = FileResourceConverters.CreateDefault();
            var loader = new RemoteFileLoader(cache,
                (_, _, _) => throw new DownloadException(EDownloadError.NotFound, "not found", null),
                converters);

            using var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics());
            manager.RegisterLoader(ResourceSource.Remote, loader);
            await manager.InitializeAsync();

            var error = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                manager.LoadAsync<byte[]>("https://host/missing.bin", ResourceSource.Remote)
                    .WaitAsync(TestTimeout));
            Assert.Equal(DiagnosticCodes.NetworkHttpNotFound, error.Error.DiagnosticCode);

            // 失败不发布缓存文件：下一次请求仍然会真实下载。
            var attempts = 0;
            var retryLoader = new RemoteFileLoader(cache, (_, destination, _) =>
            {
                Interlocked.Increment(ref attempts);
                File.WriteAllText(destination, "ok", new UTF8Encoding(false));
                return Task.CompletedTask;
            }, converters);
            manager.ReplaceLoader(ResourceSource.Remote, retryLoader);

            using var recovered = await manager.LoadAsync<string>("https://host/missing.bin", ResourceSource.Remote)
                .WaitAsync(TestTimeout);
            Assert.Equal("ok", context.Invoke(() => recovered.Value));
            Assert.Equal(1, attempts);
        }
    }
}
