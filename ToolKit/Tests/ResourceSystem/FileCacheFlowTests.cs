using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    /// <summary> 用本地填充与明确的任务顺序验证文件缓存流程，不依赖 HTTP 或真实时间延迟。 </summary>
    public sealed class FileCacheFlowTests : IDisposable
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gtcacheflow-" + Guid.NewGuid().ToString("N"));
        private readonly ManualUtcClock _clock = new ManualUtcClock();

        private string CacheDirectory => Path.Combine(_root, "http-cache-v2");

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { /* 不掩盖测试失败。 */ }
        }

        [Fact]
        public async Task StartupCleanup_KeepsNewestWithinTarget_LeavesUnrecognizedFilesAlone()
        {
            var original = await OpenCacheAsync();
            var expiring = Request("refreshed", FileValidity.ExpiresAfter(TimeSpan.FromMinutes(1)));
            var oldVersion = await original.GetOrCreateAsync(expiring, Write("old!"));
            _clock.Advance(TimeSpan.FromSeconds(30));
            var olderOtherKey = await original.GetOrCreateAsync(Request("older"), Write("old2"));
            _clock.Advance(TimeSpan.FromMinutes(1));
            var newestVersion = await original.GetOrCreateAsync(expiring, Write("new!"));
            await original.ShutdownAsync();

            var leftoverPart = Path.Combine(CacheDirectory, Guid.NewGuid().ToString("N") + ".part");
            var unknownFile = Path.Combine(CacheDirectory, "keep.txt");
            var unrelatedPart = Path.Combine(CacheDirectory, "note.part");
            var invalidCacheName = Path.Combine(CacheDirectory,
                new string('a', 64) + "_" + (DateTime.MaxValue.Ticks + 1) + "_"
                + Guid.NewGuid().ToString("N") + ".cache");
            var outsideFile = Path.Combine(_root, "business.cache");
            var nestedDirectory = Path.Combine(CacheDirectory, "unrelated");
            Directory.CreateDirectory(nestedDirectory);
            var nestedFile = Path.Combine(nestedDirectory, "keep.part");
            foreach (var path in new[] { leftoverPart, unknownFile, unrelatedPart, invalidCacheName, outsideFile, nestedFile })
            {
                await File.WriteAllTextAsync(path, "untouched");
            }

            var restarted = await OpenCacheAsync(startupTargetBytes: 4);
            try
            {
                Assert.False(File.Exists(oldVersion));
                Assert.False(File.Exists(olderOtherKey));
                Assert.False(File.Exists(leftoverPart));
                Assert.True(File.Exists(newestVersion));
                foreach (var path in new[] { unknownFile, unrelatedPart, invalidCacheName, outsideFile, nestedFile })
                {
                    Assert.True(File.Exists(path), path);
                }
                var hit = await restarted.GetOrCreateAsync(expiring,
                    (_, _) => throw new InvalidOperationException("启动恢复后的有效文件应直接命中"));
                Assert.Equal(newestVersion, hit);
            }
            finally
            {
                await restarted.ShutdownAsync();
            }
        }

        [Fact]
        public async Task FailedValidation_CleansTemporaryFileBeforeRetryingSameIdentity()
        {
            var cache = await OpenCacheAsync();
            var request = Request("validated", expectedSha256: Sha256("good"));
            try
            {
                var error = await Assert.ThrowsAsync<ResourceLoadException>(
                    () => cache.GetOrCreateAsync(request, Write("evil")));
                Assert.Equal(DiagnosticCodes.CacheIntegrityFailed, error.Error.DiagnosticCode);
                Assert.Empty(Directory.GetFiles(CacheDirectory, "*.part"));
                Assert.Empty(Directory.GetFiles(CacheDirectory, "*.cache"));

                var path = await cache.GetOrCreateAsync(request, Write("good"));
                Assert.Equal("good", await File.ReadAllTextAsync(path));
                Assert.Single(Directory.GetFiles(CacheDirectory, "*.cache"));
            }
            finally
            {
                await cache.ShutdownAsync();
            }
        }

        [Fact]
        public async Task SharedFill_ChecksEachCallersContentDeclaration()
        {
            var cache = await OpenCacheAsync();
            var finishFill = Signal();
            var fillStarted = Signal();
            var fillCount = 0;
            async Task Fill(string path, CancellationToken token)
            {
                Interlocked.Increment(ref fillCount);
                fillStarted.TrySetResult(true);
                await finishFill.Task.ConfigureAwait(false);
                await File.WriteAllTextAsync(path, "content", token).ConfigureAwait(false);
            }

            try
            {
                var first = cache.GetOrCreateAsync(Request("shared"), Fill);
                await fillStarted.Task.WaitAsync(TestTimeout);
                var matching = cache.GetOrCreateAsync(
                    Request("shared", expectedSha256: Sha256("content")), Fill);
                var mismatching = cache.GetOrCreateAsync(
                    Request("shared", expectedSha256: Sha256("another")), Fill);
                finishFill.TrySetResult(true);

                Assert.Equal(await first.WaitAsync(TestTimeout), await matching.WaitAsync(TestTimeout));
                var error = await Assert.ThrowsAsync<ResourceLoadException>(() => mismatching.WaitAsync(TestTimeout));
                Assert.Equal(DiagnosticCodes.CacheIntegrityFailed, error.Error.DiagnosticCode);
                Assert.Equal(1, fillCount);
                Assert.Single(Directory.GetFiles(CacheDirectory, "*.cache"));
            }
            finally
            {
                finishFill.TrySetResult(true);
                await cache.ShutdownAsync();
            }
        }

        [Fact]
        public async Task LocalHit_RejectsDifferentValidityRuleForSameIdentity()
        {
            var cache = await OpenCacheAsync();
            try
            {
                var first = await cache.GetOrCreateAsync(Request("same"), Write("content"));
                var error = await Assert.ThrowsAsync<ResourceLoadException>(() => cache.GetOrCreateAsync(
                    Request("same", FileValidity.ExpiresAfter(TimeSpan.FromMinutes(1))),
                    (_, _) => throw new InvalidOperationException("冲突请求不应发起填充")));
                Assert.Equal(DiagnosticCodes.CacheIdentityConflict, error.Error.DiagnosticCode);
                Assert.True(File.Exists(first));
            }
            finally
            {
                await cache.ShutdownAsync();
            }
        }

        [Fact]
        public async Task Shutdown_WaitsForActualFillCleanup_EvenWhenOneShutdownWaitIsCancelled()
        {
            var cache = await OpenCacheAsync();
            var partCreated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationObserved = Signal();
            var finishFill = Signal();
            async Task Fill(string path, CancellationToken token)
            {
                using var registration = token.Register(() => cancellationObserved.TrySetResult(true));
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                var bytes = Encoding.UTF8.GetBytes("partial");
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                partCreated.TrySetResult(path);
                // 模拟取消已经传到 I/O，但操作与句柄尚未完成收尾。
                await finishFill.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }

            var request = cache.GetOrCreateAsync(Request("slow"), Fill);
            try
            {
                var partPath = await partCreated.Task.WaitAsync(TestTimeout);
                using var callerCancellation = new CancellationTokenSource();
                var firstShutdownWait = cache.ShutdownAsync(callerCancellation.Token);
                await cancellationObserved.Task.WaitAsync(TestTimeout);
                Assert.False(firstShutdownWait.IsCompleted);

                callerCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstShutdownWait);
                var secondShutdownWait = cache.ShutdownAsync();
                Assert.False(secondShutdownWait.IsCompleted);
                Assert.True(File.Exists(partPath));
                Assert.Empty(Directory.GetFiles(CacheDirectory, "*.cache"));

                var rejected = await Assert.ThrowsAsync<ResourceLoadException>(
                    () => cache.GetOrCreateAsync(Request("new"), Write("unused")));
                Assert.Equal(DiagnosticCodes.LifecycleSystemClosing, rejected.Error.DiagnosticCode);

                finishFill.TrySetResult(true);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TestTimeout));
                await secondShutdownWait.WaitAsync(TestTimeout);
                Assert.False(File.Exists(partPath));
                Assert.Empty(Directory.GetFiles(CacheDirectory, "*.cache"));
            }
            finally
            {
                finishFill.TrySetResult(true);
                await cache.ShutdownAsync();
            }
        }

        [Fact]
        public async Task Initialization_IsShared_AndShutdownWaitsForTheScanToExit()
        {
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllTextAsync(Path.Combine(CacheDirectory, "unknown.cache"), "unrecognized");
            using var allowScanToFinish = new ManualResetEventSlim();
            var scanPaused = Signal();
            var diagnosticFinished = Signal();
            var diagnostics = new CallbackDiagnostics(error =>
            {
                if (error.DiagnosticCode != DiagnosticCodes.CacheIndexFailed) return;
                scanPaused.TrySetResult(true);
                diagnosticFinished.TrySetResult(allowScanToFinish.Wait(TestTimeout));
            });
            var cache = new FileCache(new FileCacheOptions { Directory = _root }, diagnostics);
            using var callerCancellation = new CancellationTokenSource();
            var firstWait = cache.InitializeAsync(callerCancellation.Token);
            try
            {
                await scanPaused.Task.WaitAsync(TestTimeout);
                var secondWait = cache.InitializeAsync();
                Assert.False(firstWait.IsCompleted);
                Assert.False(secondWait.IsCompleted);

                callerCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstWait.WaitAsync(TestTimeout));
                Assert.False(secondWait.IsCompleted);

                var shutdown = cache.ShutdownAsync();
                Assert.False(shutdown.IsCompleted);
                Assert.False(diagnosticFinished.Task.IsCompleted);
                await Assert.ThrowsAsync<InvalidOperationException>(() => cache.InitializeAsync());
                var rejectedDuringShutdown = await Assert.ThrowsAsync<ResourceLoadException>(
                    () => cache.GetOrCreateAsync(Request("during-scan"), Write("unused")));
                Assert.Equal(DiagnosticCodes.LifecycleSystemClosing, rejectedDuringShutdown.Error.DiagnosticCode);

                allowScanToFinish.Set();
                await shutdown.WaitAsync(TestTimeout);
                Assert.True(await diagnosticFinished.Task.WaitAsync(TestTimeout), "扫描应由测试放行，不应等到超时退出");
                // 关闭可以取消扫描；若扫描先完成，也不能重新开放缓存。
                try { await secondWait.WaitAsync(TestTimeout); }
                catch (OperationCanceledException) { }
                await Assert.ThrowsAsync<InvalidOperationException>(() => cache.InitializeAsync());
                var rejectedAfterShutdown = await Assert.ThrowsAsync<ResourceLoadException>(
                    () => cache.GetOrCreateAsync(Request("after-scan"), Write("unused")));
                Assert.Equal(DiagnosticCodes.LifecycleSystemClosing, rejectedAfterShutdown.Error.DiagnosticCode);
            }
            finally
            {
                allowScanToFinish.Set();
                await cache.ShutdownAsync().WaitAsync(TestTimeout);
            }
        }

        [Fact]
        public async Task FillSynchronousWork_DoesNotBlockRequestsForOtherFiles()
        {
            var cache = await OpenCacheAsync();
            using var allowFirstFillToFinish = new ManualResetEventSlim();
            var firstFillStarted = Signal();
            Task BlockingFill(string path, CancellationToken token)
            {
                firstFillStarted.TrySetResult(true);
                if (!allowFirstFillToFinish.Wait(TestTimeout))
                    throw new TimeoutException("第一份文件的同步填充应由测试放行");
                return File.WriteAllTextAsync(path, "first", token);
            }

            var first = Task.Run(() => cache.GetOrCreateAsync(Request("blocked"), BlockingFill));
            try
            {
                await firstFillStarted.Task.WaitAsync(TestTimeout);
                // 单独启动，锁内执行 fill 的回归也只能让此任务超时，不能阻塞测试的 finally。
                var second = Task.Run(() => cache.GetOrCreateAsync(Request("independent"), Write("second")));
                var secondPath = await second.WaitAsync(TestTimeout);
                Assert.Equal("second", await File.ReadAllTextAsync(secondPath));
                Assert.False(first.IsCompleted);

                allowFirstFillToFinish.Set();
                var firstPath = await first.WaitAsync(TestTimeout);
                Assert.Equal("first", await File.ReadAllTextAsync(firstPath));
            }
            finally
            {
                allowFirstFillToFinish.Set();
                await cache.ShutdownAsync().WaitAsync(TestTimeout);
            }
        }

        private async Task<FileCache> OpenCacheAsync(long? startupTargetBytes = null)
        {
            var cache = new FileCache(new FileCacheOptions
            {
                Directory = _root,
                StartupTargetBytes = startupTargetBytes,
            }, utcNow: _clock);
            await cache.InitializeAsync();
            return cache;
        }

        private static FileRequest Request(string id, FileValidity? validity = null, string? expectedSha256 = null)
        {
            return new FileRequest(new FileIdentity("flow-tests", id, "v1", ""),
                validity ?? FileValidity.Immutable, expectedSha256: expectedSha256);
        }

        private static Func<string, CancellationToken, Task> Write(string content)
        {
            return (path, token) => File.WriteAllTextAsync(path, content, token);
        }

        private static TaskCompletionSource<bool> Signal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static string Sha256(string content)
        {
            using var algorithm = SHA256.Create();
            return string.Concat(algorithm.ComputeHash(Encoding.UTF8.GetBytes(content)).Select(b => b.ToString("x2")));
        }

        private sealed class CallbackDiagnostics : IResourceDiagnostics
        {
            private readonly Action<LoadError> _report;

            public CallbackDiagnostics(Action<LoadError> report) { _report = report; }
            public void Report(LoadError error) { _report(error); }
            public void Trace(ResourceEvent traceEvent) { }
        }
    }
}
