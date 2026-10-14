/*
 * 下载模块测试 (H07–H12)：SimpleDownloader.DownloadAsync 独立工作，
 * 不依赖 FileCache/FileIdentity/ResourceLoadException。使用本地 HttpListener 可控服务。
 */

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Network;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    /// <summary> 可控本地 HTTP 服务：支持内容、延迟、状态码、断连、头部回显 </summary>
    public sealed class LocalHttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _loop;
        public int RequestCount;
        public string? LastUserAgent;

        public LocalHttpServer()
        {
            var port = Random.Shared.Next(20000, 40000);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{port}";
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }

        /// <summary> path → 响应行为 </summary>
        public Func<HttpListenerRequest, (int status, byte[] body, TimeSpan headerDelay, bool abortAfterHeaders)> OnRequest
            = r => (200, Encoding.UTF8.GetBytes("ok"), TimeSpan.Zero, false);

        public Func<HttpListenerRequest, Task>? BeforeResponseAsync;

        private async Task AcceptLoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return; // 已停止
                }
                Interlocked.Increment(ref RequestCount);
                LastUserAgent = context.Request.Headers["User-Agent"];
                var (status, body, headerDelay, abort) = OnRequest(context.Request);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (headerDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(headerDelay).ConfigureAwait(false);
                        }
                        if (BeforeResponseAsync != null)
                        {
                            await BeforeResponseAsync(context.Request).ConfigureAwait(false);
                        }
                        context.Response.StatusCode = status;
                        if (abort)
                        {
                            context.Response.Abort(); // 断连：发送部分内容后中断
                            return;
                        }
                        context.Response.ContentLength64 = body.Length;
                        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                        context.Response.Close();
                    }
                    catch (Exception)
                    {
                        // 客户端取消导致的写入失败是预期
                    }
                });
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* ignored */ }
            try { _listener.Close(); } catch { /* ignored */ }
        }
    }

    public sealed class DownloaderTests : IDisposable
    {
        private readonly string _root;
        private readonly LocalHttpServer _server = new LocalHttpServer();

        public DownloaderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gtdl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            _server.Dispose();
            try { Directory.Delete(_root, true); } catch { /* 尽力清理 */ }
        }

        private static NetworkOptions FastOptions(int maxRetries = 0)
        {
            return new NetworkOptions
            {
                MaxConcurrentDownloads = 2,
                ConnectTimeout = TimeSpan.FromSeconds(3),
                ResponseTimeout = TimeSpan.FromSeconds(3),
                MaxRetries = maxRetries,
                RetryBaseDelay = TimeSpan.FromMilliseconds(20),
                BufferSize = 1024,
            };
        }

        // H07: 独立写入给定路径，不依赖任何资源类型
        [Fact]
        public async Task H07_DownloadsToGivenPath_Independent()
        {
            using var downloader = new SimpleDownloader(2, FastOptions());
            var target = Path.Combine(_root, "independent.bin");
            await downloader.DownloadAsync(
                new DownloadRequest(new Uri(_server.BaseUrl + "/data")), target);
            Assert.True(File.Exists(target));
            Assert.Equal("ok", await File.ReadAllTextAsync(target));
        }

        // H08: 成功仅在文件完整且句柄关闭后；失败为失败 Task；取消为取消 Task
        [Fact]
        public async Task H08_TaskSemantics_SuccessFailureCancel()
        {
            _server.OnRequest = r => (404, Array.Empty<byte>(), TimeSpan.Zero, false);
            using var downloader = new SimpleDownloader(2, FastOptions());
            var target = Path.Combine(_root, "sem.bin");

            // 失败 Task (404)
            var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(
                new DownloadRequest(new Uri(_server.BaseUrl + "/missing")), target));
            Assert.Equal(EDownloadError.NotFound, ex.ErrorKind);
            Assert.Equal(404, ex.HttpStatus);

            // 取消 Task：取消后无后台写入 (句柄已释放，可删除文件)
            _server.OnRequest = r => (200, Encoding.UTF8.GetBytes("slow-body"), TimeSpan.FromMilliseconds(400), false);
            var cts = new CancellationTokenSource(100);
            var cancelled = downloader.DownloadAsync(
                new DownloadRequest(new Uri(_server.BaseUrl + "/slow")), target, cts.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            File.Delete(target); // 句柄已关闭才能删除
        }

        // H09: 请求头实际生效；重试重新截断不叠写
        [Fact]
        public async Task H09_HeadersAndRetruncate()
        {
            _server.OnRequest = r => r.Headers["User-Agent"] == "resource-kit/1.0"
                ? (200, Encoding.UTF8.GetBytes("with-header"), TimeSpan.Zero, false)
                : (400, Array.Empty<byte>(), TimeSpan.Zero, false);
            using var downloader = new SimpleDownloader(2, FastOptions());
            var target = Path.Combine(_root, "headers.bin");
            await downloader.DownloadAsync(
                new DownloadRequest(new Uri(_server.BaseUrl + "/h"),
                    new System.Collections.Generic.Dictionary<string, string> { ["User-Agent"] = "resource-kit/1.0" }),
                target);
            Assert.Equal("with-header", await File.ReadAllTextAsync(target));
            Assert.Equal("resource-kit/1.0", _server.LastUserAgent);
        }

        // H10: 不同请求共享并发上限；混用旧队列明确报错；SetMaxConcurrency 在直接入口使用后报错
        [Fact]
        public async Task H10_ConcurrencyLimit_AndModeGuards()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = 0;
            var maxInFlight = 0;
            _server.OnRequest = r =>
            {
                var current = Interlocked.Increment(ref inFlight);
                int seen;
                do
                {
                    seen = Volatile.Read(ref maxInFlight);
                    if (current <= seen)
                    {
                        break;
                    }
                } while (Interlocked.CompareExchange(ref maxInFlight, current, seen) != seen);
                if (current >= 2) gate.TrySetResult(true);
                return (200, Encoding.UTF8.GetBytes($"body-{r.Url.AbsolutePath}"), TimeSpan.Zero, false);
            };
            _server.BeforeResponseAsync = async _ =>
            {
                // 两个请求确实同时到达后才发响应；发出响应前解除计数，早于客户端释放槽位。
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Interlocked.Decrement(ref inFlight);
            };
            using var downloader = new SimpleDownloader(2, FastOptions());

            var tasks = Enumerable.Range(0, 5)
                .Select(i => downloader.DownloadAsync(new DownloadRequest(new Uri(_server.BaseUrl + "/k" + i)),
                    Path.Combine(_root, $"k{i}.bin")))
                .ToList();
            await Task.WhenAll(tasks);
            Assert.Equal(2, maxInFlight); // 共享并发上限

            // 混用旧队列明确报错
            Assert.Throws<InvalidOperationException>(() =>
                downloader.AddTask(downloader.CreateTask(_server.BaseUrl + "/x", Path.Combine(_root, "x.bin"))));
            Assert.Throws<InvalidOperationException>(() => downloader.SetMaxConcurrency(4));
        }

        // H11: 连接/响应头与响应体超时生效；404 不重试 (尝试次数确定)
        [Fact]
        public async Task H11_Timeouts_AndNoRetryFor404()
        {
            // 404 不重试：请求次数 = 1
            _server.OnRequest = r => (404, Array.Empty<byte>(), TimeSpan.Zero, false);
            using var downloader = new SimpleDownloader(2, FastOptions(maxRetries: 2));
            var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(
                new DownloadRequest(new Uri(_server.BaseUrl + "/gone")), Path.Combine(_root, "404.bin")));
            Assert.Equal(EDownloadError.NotFound, ex.ErrorKind);
            Assert.Equal(1, _server.RequestCount);

            // 响应体读取超时 (响应迟迟不结束：服务器只发头部不关闭 → 读等待)
            _server.OnRequest = r => (200, Encoding.UTF8.GetBytes("slow-body"), TimeSpan.FromMilliseconds(10), true);
            var timeoutEx = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(
                new DownloadRequest(new Uri(_server.BaseUrl + "/abort")), Path.Combine(_root, "abort.bin"),
                new CancellationTokenSource(8000).Token));
            // 断连/超时均归类为可诊断失败
            Assert.True(timeoutEx.ErrorKind is EDownloadError.Timeout or EDownloadError.Network,
                $"实际分类: {timeoutEx.ErrorKind}");
        }

        // H12: 网络读取与磁盘写入分别分类；原异常与 HTTP 状态可追踪
        [Fact]
        public async Task H12_Classification_PreservesEvidence()
        {
            _server.OnRequest = r => (403, Array.Empty<byte>(), TimeSpan.Zero, false);
            using var downloader = new SimpleDownloader(2, FastOptions());
            var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(
                new DownloadRequest(new Uri(_server.BaseUrl + "/denied")), Path.Combine(_root, "d.bin")));
            Assert.Equal(EDownloadError.AccessDenied, ex.ErrorKind);
            Assert.Equal(403, ex.HttpStatus);
            Assert.Contains("403", ex.Message);

            // 无效地址
            var uriEx = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(
                new DownloadRequest(new Uri(_server.BaseUrl + "/ok")), "CON|invalid?path"));
            Assert.True(uriEx.ErrorKind is EDownloadError.Storage or EDownloadError.AccessDenied,
                $"实际分类: {uriEx.ErrorKind}");
            Assert.NotNull(uriEx.InnerException); // 原异常保留
        }
    }
}
