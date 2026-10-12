using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class ResourceStartupTests : IDisposable
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "resource-startup-" + Guid.NewGuid().ToString("N"));
        private string CacheDirectory => Path.Combine(_root, "http-cache-v2");

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Fact]
        public async Task BeforeInitialization_LoadAndRentAreRejectedWithoutChangingState()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader { Factory = key => new FakePrefab(key) };
            var factory = new FakeInstanceFactory();
            manager.RegisterLoader(ResourceSource.Local, loader);
            manager.RegisterFactory("instances", factory);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.LoadAsync<FakePrefab>("asset", ResourceSource.Local).WaitAsync(TestTimeout));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.RentAsync<FakeInstance>("asset", ResourceSource.Local, factory: "instances").WaitAsync(TestTimeout));

            var beforeInitialization = manager.GetSnapshot();
            Assert.False(manager.IsInitialized);
            Assert.False(beforeInitialization.IsInitialized);
            Assert.Empty(beforeInitialization.ResourceRows);
            Assert.Empty(beforeInitialization.PoolRows);
            Assert.Equal(0, loader.ResolveCount);
            Assert.Equal(0, loader.LoadCount);
            Assert.Equal(0, factory.CreateCount);

            var configurations = 0;
            await manager.InitializeAsync(_ => configurations++);
            await manager.InitializeAsync(); // 初始化幂等，不靠第一次请求推进生命周期。
            Assert.Throws<InvalidOperationException>(() => { _ = manager.InitializeAsync(_ => configurations++); });
            Assert.Equal(1, configurations);
            Assert.True(manager.IsInitialized);
            Assert.True(manager.GetSnapshot().IsInitialized);
            var reference = await manager.LoadAsync<FakePrefab>("asset", ResourceSource.Local).WaitAsync(TestTimeout);
            Assert.Equal("asset", reference.Value.Name);
            reference.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task MissingSourceFailure_AllowsRegisteringSourceAndLoadingAgain()
        {
            var manager = V2Test.NewManager(out _, out _);
            await manager.InitializeAsync();

            var missing = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                manager.LoadAsync<FakeAsset>("asset", ResourceSource.Remote).WaitAsync(TestTimeout));
            Assert.Equal(DiagnosticCodes.LoaderNotRegistered, missing.Error.DiagnosticCode);
            Assert.True(manager.GetSnapshot().IsInitialized);

            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Remote, loader);
            Assert.Throws<InvalidOperationException>(() =>
                manager.RegisterLoader(ResourceSource.Remote, new FakeLoader()));
            var reference = await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Remote).WaitAsync(TestTimeout);
            Assert.Equal(1, loader.LoadCount);
            reference.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task Running_AllowsNewFactoryAndRejectsDuplicateFactory()
        {
            var manager = V2Test.NewManager(out _, out _);
            manager.RegisterLoader(ResourceSource.Local,
                new FakeLoader { Factory = key => new FakePrefab(key) });
            await manager.InitializeAsync();

            var factory = new FakeInstanceFactory();
            manager.RegisterFactory("instances", factory);
            Assert.Throws<InvalidOperationException>(() =>
                manager.RegisterFactory("instances", new FakeInstanceFactory()));
            var lease = await manager.RentAsync<FakeInstance>(
                "asset", ResourceSource.Local, factory: "instances").WaitAsync(TestTimeout);
            Assert.Equal(1, factory.CreateCount);
            lease.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => factory.DestroyCount == 1);
            Assert.Equal(1, factory.DestroyCount);
        }

        [Fact]
        public async Task Disposed_RejectsNewRegistrationsAndInitialization()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var reference = await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local).WaitAsync(TestTimeout);
            manager.Dispose();
            Assert.True(manager.GetSnapshot().IsDisposed);
            Assert.True(manager.GetSnapshot().IsInitialized);
            Assert.Throws<ObjectDisposedException>(() => manager.RegisterLoader(ResourceSource.Remote, new FakeLoader()));
            Assert.Throws<ObjectDisposedException>(() => manager.RegisterFactory("instances", new FakeInstanceFactory()));
            Assert.Throws<ObjectDisposedException>(() => { _ = manager.InitializeAsync(); });

            reference.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.True(manager.GetSnapshot().IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => manager.RegisterLoader(ResourceSource.Remote, new FakeLoader()));
            Assert.Throws<ObjectDisposedException>(() => manager.RegisterFactory("instances", new FakeInstanceFactory()));
            Assert.Throws<ObjectDisposedException>(() => { _ = manager.InitializeAsync(); });
        }

        [Fact]
        public async Task Initialize_PreparesFileCacheBeforeRemoteRequestsAreAccepted()
        {
            var cache = new FileCache(new FileCacheOptions { Directory = _root });
            var manager = new ResourceManager(ImmediateExecutionContext.Instance, fileCache: cache);
            var downloads = 0;
            var loader = new RemoteFileLoader(cache, async (_, path, token) =>
            {
                Interlocked.Increment(ref downloads);
                await File.WriteAllTextAsync(path, "downloaded", token);
            }, DecoderRegistry.CreateDefault());

            try
            {
                Assert.False(Directory.Exists(CacheDirectory));
                await manager.InitializeAsync(resources => resources.RegisterLoader(ResourceSource.Remote, loader))
                    .WaitAsync(TestTimeout);
                Assert.True(manager.IsInitialized);
                Assert.True(Directory.Exists(CacheDirectory));

                using var bytes = await manager.LoadAsync<byte[]>("https://example.test/asset", ResourceSource.Remote)
                    .WaitAsync(TestTimeout);
                using var text = await manager.LoadAsync<string>("https://example.test/asset", ResourceSource.Remote)
                    .WaitAsync(TestTimeout);
                Assert.Equal("downloaded", Encoding.UTF8.GetString(bytes.Value));
                Assert.Equal("downloaded", text.Value);
                Assert.Equal(1, downloads); // 不同内存表示复用已准备好的本地缓存。
            }
            finally
            {
                manager.Dispose();
                await cache.DisposeAsync().WaitAsync(TestTimeout);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task InitializationFailure_KeepsManagerUnavailableAndDoesNotRetry(bool failConfiguration)
        {
            if (!failConfiguration)
            {
                Directory.CreateDirectory(_root);
                await File.WriteAllTextAsync(CacheDirectory, "a file blocks the cache directory");
            }
            var cache = new FileCache(new FileCacheOptions { Directory = _root });
            var manager = new ResourceManager(ImmediateExecutionContext.Instance, fileCache: cache);
            var loader = new FakeLoader();
            var configurationError = new InvalidOperationException("configuration failed");
            var configurations = 0;

            try
            {
                var initialization = manager.InitializeAsync(resources =>
                {
                    configurations++;
                    resources.RegisterLoader(ResourceSource.Local, loader);
                    if (failConfiguration) throw configurationError;
                });
                var error = await Record.ExceptionAsync(() => initialization.WaitAsync(TestTimeout));
                if (failConfiguration) Assert.Same(configurationError, error);
                else Assert.IsAssignableFrom<IOException>(error);
                Assert.False(manager.IsInitialized);
                Assert.False(manager.GetSnapshot().IsInitialized);

                var repeated = await Record.ExceptionAsync(() => manager.InitializeAsync().WaitAsync(TestTimeout));
                Assert.Same(error, repeated);
                Assert.Equal(1, configurations);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local).WaitAsync(TestTimeout));
                Assert.Equal(0, loader.ResolveCount);
                Assert.Equal(0, loader.LoadCount);
                if (failConfiguration) Assert.False(Directory.Exists(CacheDirectory));
            }
            finally
            {
                manager.Dispose();
                await cache.DisposeAsync().WaitAsync(TestTimeout);
            }
        }

        [Fact]
        public async Task InitializationInProgress_RejectsRequests_AndCallerCancellationOnlyStopsItsWait()
        {
            using var finishScan = new ManualResetEventSlim();
            using var callerCancellation = new CancellationTokenSource();
            var scanPaused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scanReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cache = CreatePausedCache(finishScan, scanPaused, scanReleased);
            var manager = new ResourceManager(ImmediateExecutionContext.Instance, fileCache: cache);
            var loader = new FakeLoader();
            var configurations = 0;
            var firstWait = manager.InitializeAsync(resources =>
            {
                configurations++;
                resources.RegisterLoader(ResourceSource.Local, loader);
            }, callerCancellation.Token);

            try
            {
                await scanPaused.Task.WaitAsync(TestTimeout);
                var secondWait = manager.InitializeAsync();
                Assert.False(firstWait.IsCompleted);
                Assert.False(secondWait.IsCompleted);
                Assert.False(manager.IsInitialized);
                var prematureRequest = manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local);
                Assert.True(prematureRequest.IsCompleted, "请求应直接拒绝，不能在请求内部隐式等待初始化");
                await Assert.ThrowsAsync<InvalidOperationException>(() => prematureRequest);
                Assert.Equal(0, loader.ResolveCount);

                callerCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstWait.WaitAsync(TestTimeout));
                Assert.False(secondWait.IsCompleted);
                Assert.Equal(1, configurations);

                finishScan.Set();
                await secondWait.WaitAsync(TestTimeout);
                Assert.True(await scanReleased.Task.WaitAsync(TestTimeout));
                Assert.True(manager.IsInitialized);
                using var reference = await manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local).WaitAsync(TestTimeout);
                Assert.Equal("asset", reference.Value.Name);
            }
            finally
            {
                finishScan.Set();
                manager.Dispose();
                await cache.DisposeAsync().WaitAsync(TestTimeout);
            }
        }

        [Fact]
        public async Task DisposeDuringInitialization_DoesNotOpenManagerWhenFileScanFinishes()
        {
            using var finishScan = new ManualResetEventSlim();
            var scanPaused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scanReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cache = CreatePausedCache(finishScan, scanPaused, scanReleased);
            var manager = new ResourceManager(ImmediateExecutionContext.Instance, fileCache: cache);
            var initialization = manager.InitializeAsync();

            try
            {
                await scanPaused.Task.WaitAsync(TestTimeout);
                manager.Dispose();
                Assert.True(manager.IsDisposed);
                Assert.False(manager.IsInitialized);
                var error = await Record.ExceptionAsync(() => initialization.WaitAsync(TestTimeout));
                Assert.True(error is OperationCanceledException || error is ObjectDisposedException);

                finishScan.Set();
                await cache.InitializeAsync().WaitAsync(TestTimeout); // 实际磁盘扫描结束也不能重新开放管理器。
                Assert.True(await scanReleased.Task.WaitAsync(TestTimeout));
                Assert.False(manager.IsInitialized);
                Assert.False(manager.GetSnapshot().IsInitialized);
                Assert.Throws<ObjectDisposedException>(() => { _ = manager.InitializeAsync(); });
                var rejected = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                    manager.LoadAsync<FakeAsset>("asset", ResourceSource.Local).WaitAsync(TestTimeout));
                Assert.Equal(DiagnosticCodes.LifecycleManagerDisposed, rejected.Error.DiagnosticCode);
            }
            finally
            {
                finishScan.Set();
                manager.Dispose();
                await cache.DisposeAsync().WaitAsync(TestTimeout);
            }
        }

        private FileCache CreatePausedCache(ManualResetEventSlim finishScan,
            TaskCompletionSource<bool> scanPaused, TaskCompletionSource<bool> scanReleased)
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(Path.Combine(CacheDirectory, "unknown.cache"), "unrecognized");
            return new FileCache(new FileCacheOptions { Directory = _root }, new CallbackDiagnostics(error =>
            {
                if (error.DiagnosticCode != DiagnosticCodes.CacheIndexFailed) return;
                scanPaused.TrySetResult(true);
                scanReleased.TrySetResult(finishScan.Wait(TestTimeout));
            }));
        }

        private sealed class CallbackDiagnostics : IResourceDiagnostics
        {
            private readonly Action<LoadError> _report;
            public CallbackDiagnostics(Action<LoadError> report) { _report = report; }
            public void Report(LoadError error) => _report(error);
            public void Trace(ResourceEvent traceEvent) { }
        }
    }
}
