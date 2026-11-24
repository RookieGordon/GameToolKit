using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.TestTools;
using UnityToolKit.Runtime.ResourceSystem;
using RS = ToolKit.Tools.Common.ResourceSystem;
using Object = UnityEngine.Object;

namespace Tests.PlayMode.ResourceSystem
{
    public class RemoteFileLoaderConfigPlayModeTests : ResourcePlayModeTestBase
    {
        private const string Address = "https://resource-system-tests.invalid/configured.txt";
        private readonly List<RS> _systems = new List<RS>();
        private readonly List<FileCache> _externalCaches = new List<FileCache>();

        [UnityTearDown]
        public IEnumerator CloseExternalOwners()
        {
            foreach (var system in _systems) yield return Await(system.DisposeAsync().AsTask());
            foreach (var cache in _externalCaches) yield return Await(cache.StopAsync());
            _systems.Clear();
            _externalCaches.Clear();
        }

        [UnityTest]
        public IEnumerator AutomaticConfigurationCreatesDirectoryOnlyDuringInitializationAndClosesItsServices()
        {
            var directory = Path.Combine(CreateTempDirectory(), "automatic-cache");
            var asset = NewAsset(new RemoteFileLoaderConfig { CacheDirectory = directory });
            var options = asset.ConvertToOptions();
            var system = NewSystem(options);
            Assert.That(options.Loaders[ResourceSource.Remote].Loader, Is.TypeOf<RemoteFileLoader>());
            Assert.That(options.Initializers.Count, Is.EqualTo(1));
            Assert.That(options.ShutdownActions.Count, Is.EqualTo(1));
            Assert.That(Directory.Exists(directory), Is.False, "配置转换只固化参数，不执行缓存清理。");

            yield return Await(system.InitializeAsync());
            Assert.That(Directory.Exists(directory), Is.True);
            File.WriteAllBytes(new HashedPathConverter(directory).RemoteUrlToLocalPath(Address), Encoding.UTF8.GetBytes("cached-config"));
            var loading = system.LoadAsync<string>(ResourceSource.Remote, Address);
            try
            {
                yield return Await(loading);
                Assert.That(loading.Result.Value, Is.EqualTo("cached-config"));
            }
            finally { ReleaseWhenCompleted(loading); }

            yield return Await(system.DisposeAsync().AsTask());
            var afterDispose = system.LoadAsync<string>(ResourceSource.Remote, Address);
            yield return Finish(afterDispose);
            var error = Assert.Throws<ResourceException>(() => afterDispose.GetAwaiter().GetResult());
            Assert.That(error.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.LifecycleSystemDisposed));
        }

        [UnityTest]
        public IEnumerator DefaultDownloaderFetchesFromLoopbackThenReusesItsPublishedCache()
        {
            var directory = Path.Combine(CreateTempDirectory(), "http-cache");
            var asset = NewAsset(new RemoteFileLoaderConfig
            {
                CacheDirectory = directory, MaxConcurrentDownloads = 1, TimeoutSeconds = 8
            });
            var system = NewSystem(asset.ConvertToOptions());
            yield return Await(system.InitializeAsync());

            using (var server = new LoopbackResponse("本机下载 / configured loader"))
            {
                var first = system.LoadAsync<string>(ResourceSource.Remote, server.Url);
                try
                {
                    yield return Await(first);
                    Assert.That(first.Result.Value, Is.EqualTo("本机下载 / configured loader"));
                    yield return Await(server.Served);
                    Assert.That(server.Served.Result, Does.StartWith("GET /resource.txt "));
                }
                finally { ReleaseWhenCompleted(first); }

                server.Dispose(); // 再次读取必须命中已发布的磁盘文件。
                yield return Await(system.UnloadAsync());
                var second = system.LoadAsync<string>(ResourceSource.Remote, server.Url);
                try
                {
                    yield return Await(second);
                    Assert.That(second.Result.Value, Is.EqualTo("本机下载 / configured loader"));
                }
                finally { ReleaseWhenCompleted(second); }
                Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
                Assert.That(File.ReadAllText(new HashedPathConverter(directory).RemoteUrlToLocalPath(server.Url)),
                    Is.EqualTo("本机下载 / configured loader"));
            }
        }

        [UnityTest]
        public IEnumerator DeferredRemoteHasNoStartupSideEffectsAndLateCoreCacheRemainsExternallyOwned()
        {
            var directory = Path.Combine(CreateTempDirectory(), "late-cache");
            var remote = new RemoteFileLoaderConfig
            {
                InitializeOnStartup = false, CacheDirectory = directory, TimeoutSeconds = 0
            };
            var asset = NewAsset(remote);
            var options = asset.ConvertToOptions();
            var system = NewSystem(options);
            Assert.That(options.Loaders[ResourceSource.Remote].Loader, Is.Null);
            Assert.That(options.Initializers, Is.Empty);
            Assert.That(options.ShutdownActions, Is.Empty);
            yield return Await(system.InitializeAsync());
            Assert.That(Directory.Exists(directory), Is.False);

            var fetches = 0;
            var cache = OwnCache(new FileCache(
                new HashedPathConverter(directory),
                (_, path, progress, token) =>
                {
                    Interlocked.Increment(ref fetches);
                    File.WriteAllText(path, "late-owner", new UTF8Encoding(false));
                    return Task.CompletedTask;
                },
                startupTargetBytes: 1024));
            Assert.That(Directory.Exists(directory), Is.False);
            yield return Await(cache.InitializeAsync());
            system.RegisterLoader(ResourceSource.Remote, new RemoteFileLoader(cache));
            var loading = system.LoadAsync<string>(ResourceSource.Remote, Address);
            try
            {
                yield return Await(loading);
                Assert.That(loading.Result.Value, Is.EqualTo("late-owner"));
            }
            finally { ReleaseWhenCompleted(loading); }
            yield return Await(system.DisposeAsync().AsTask());

            // 外部接入的缓存不属于资源系统，系统退出后仍可继续工作，直到其拥有者调用 StopAsync。
            var stillOwned = cache.GetFileAsync(Address, null, CancellationToken.None);
            yield return Await(stillOwned);
            Assert.That(File.ReadAllText(stillOwned.Result), Is.EqualTo("late-owner"));
            Assert.That(fetches, Is.EqualTo(1));
            yield return Await(cache.StopAsync());
        }

        [UnityTest]
        public IEnumerator FailedAutomaticInitializationRetriesTheSameConfiguredServices()
        {
            var directory = Path.Combine(CreateTempDirectory(), "blocked-cache");
            File.WriteAllText(directory, "a file prevents directory creation");
            var asset = NewAsset(new RemoteFileLoaderConfig { CacheDirectory = directory });
            var options = asset.ConvertToOptions();
            var loader = options.Loaders[ResourceSource.Remote].Loader;
            var system = NewSystem(options);
            var failed = system.InitializeAsync();
            yield return Finish(failed);
            Assert.That(failed.IsFaulted, Is.True);
            Assert.That(failed.Exception.GetBaseException(), Is.InstanceOf<IOException>());
            Assert.That(system.IsInitialized, Is.False);

            File.Delete(directory);
            var retry = system.InitializeAsync();
            Assert.That(retry, Is.Not.SameAs(failed));
            yield return Await(retry);
            Assert.That(system.IsInitialized, Is.True);
            Assert.That(options.Loaders[ResourceSource.Remote].Loader, Is.SameAs(loader));
            File.WriteAllBytes(new HashedPathConverter(directory).RemoteUrlToLocalPath(Address), Encoding.UTF8.GetBytes("retried"));
            var loading = system.LoadAsync<string>(ResourceSource.Remote, Address);
            try
            {
                yield return Await(loading);
                Assert.That(loading.Result.Value, Is.EqualTo("retried"));
            }
            finally { ReleaseWhenCompleted(loading); }
        }

        [UnityTest]
        public IEnumerator LastSourceEntryAloneCreatesAndInitializesItsRemoteDependencies()
        {
            var root = CreateTempDirectory();
            var replacedDirectory = Path.Combine(root, "replaced");
            var selectedDirectory = Path.Combine(root, "selected");
            var asset = NewAsset(new RemoteFileLoaderConfig { CacheDirectory = replacedDirectory });
            asset.Loaders.Add(new RemoteFileLoaderConfig { CacheDirectory = selectedDirectory });
            var options = asset.ConvertToOptions();
            Assert.That(options.Initializers.Count, Is.EqualTo(1));
            Assert.That(options.ShutdownActions.Count, Is.EqualTo(1));
            var system = NewSystem(options);
            yield return Await(system.InitializeAsync());
            Assert.That(Directory.Exists(selectedDirectory), Is.True);
            Assert.That(Directory.Exists(replacedDirectory), Is.False);

            asset.Loaders.Add(new LoaderConfig { Source = ResourceSource.Remote, InitializeOnStartup = false });
            var deferred = asset.ConvertToOptions();
            Assert.That(deferred.Loaders[ResourceSource.Remote].Loader, Is.Null);
            Assert.That(deferred.Initializers, Is.Empty);
            Assert.That(deferred.ShutdownActions, Is.Empty);
            yield return Await(NewSystem(deferred).InitializeAsync());
            Assert.That(Directory.Exists(replacedDirectory), Is.False);
        }

        [UnityTest]
        public IEnumerator ShutdownActionsUseConstructionSnapshotRunInReverseAndContinueAfterFailure()
        {
            var order = new List<string>();
            var expected = new InvalidOperationException("owned-service-close-failed");
            var options = new ResourceSystemOptions { ExecutionContext = new UnityExecutionContext() }
                .AddLoader(ResourceSource.Local, new ReleaseRecordingLoader(() => order.Add("release")));
            options.ShutdownActions.Add(() => { order.Add("first"); return Task.CompletedTask; });
            options.ShutdownActions.Add(() => { order.Add("second"); throw expected; });
            options.ShutdownActions.Add(() => { order.Add("third"); return Task.CompletedTask; });
            var system = NewSystem(options);
            options.ShutdownActions.Clear();
            options.ShutdownActions.Add(() => { order.Add("changed-after-construction"); return Task.CompletedTask; });
            yield return Await(system.InitializeAsync());
            var loading = system.LoadAsync<string>(ResourceSource.Local, "value");
            try { yield return Await(loading); }
            finally { ReleaseWhenCompleted(loading); }

            var disposal = system.DisposeAsync().AsTask();
            yield return Finish(disposal);
            var error = Assert.Throws<AggregateException>(() => disposal.GetAwaiter().GetResult());
            Assert.That(error.InnerExceptions, Does.Contain(expected));
            CollectionAssert.AreEqual(new[] { "release", "third", "second", "first" }, order);
            yield return Await(system.DisposeAsync().AsTask());
            Assert.That(order.Count, Is.EqualTo(4));
        }

        [UnityTest]
        public IEnumerator ManagerShutdownAwaitsOwnedServicesAndReusesTheSameTask()
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var options = new ResourceSystemOptions { ExecutionContext = new UnityExecutionContext() };
            options.ShutdownActions.Add(async () =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult(true);
                await ready.Task;
            });
            var system = NewSystem(options);
            var manager = NewManager(system);
            yield return Await(manager.InitializeAsync());
            try
            {
                var shutdown = manager.ShutdownAsync();
                yield return Await(entered.Task);
                Assert.That(shutdown.IsCompleted, Is.False);
                Assert.That(manager.ShutdownAsync(), Is.SameAs(shutdown));
                Assert.That(manager.IsInitialized, Is.False);
                ready.TrySetResult(true);
                yield return Await(shutdown);
                Assert.That(calls, Is.EqualTo(1));
                Assert.Throws<ObjectDisposedException>(() => manager.InitializeAsync());
            }
            finally { ready.TrySetResult(true); }
        }

        [UnityTest]
        public IEnumerator DestroyingManagerClosesItsConfiguredRemoteServices()
        {
            var directory = Path.Combine(CreateTempDirectory(), "manager-cache");
            var options = NewAsset(new RemoteFileLoaderConfig { CacheDirectory = directory }).ConvertToOptions();
            var closed = 0;
            // 逆序关闭：先完成远程缓存和下载器的关闭，再记录整个关闭已到达此处。
            options.ShutdownActions.Insert(0, () => { Interlocked.Increment(ref closed); return Task.CompletedTask; });
            var system = NewSystem(options);
            var manager = NewManager(system);
            yield return Await(manager.InitializeAsync());
            Object.Destroy(manager.gameObject);
            yield return Until(() => Volatile.Read(ref closed) == 1);
            Assert.That(system.IsDisposed, Is.True);
        }

        [UnityTest]
        public IEnumerator ZeroRetrySnapshotDoesNotRetryAfterTheConfigurationIsChanged()
        {
            var directory = Path.Combine(CreateTempDirectory(), "zero-retry");
            var remote = new RemoteFileLoaderConfig
            {
                CacheDirectory = directory, MaxRetries = 0, RetryDelayMs = 0,
                MaxRetryDelayMs = 0, TimeoutSeconds = 0
            };
            var system = NewSystem(NewAsset(remote).ConvertToOptions());
            remote.MaxRetries = 1;
            yield return Await(system.InitializeAsync());
            using (var log = new DownloadFailureLogScope())
            using (var server = new LoopbackResponse("after-retry", 500, 200))
            {
                var loading = system.LoadAsync<string>(ResourceSource.Remote, server.Url);
                try
                {
                    yield return Finish(loading);
                    var failure = Assert.Throws<ResourceException>(() => loading.GetAwaiter().GetResult());
                    Assert.That(failure.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.NetworkHttpServerError));
                    Assert.That(server.RequestCount, Is.EqualTo(1));
                    Assert.That(log.Errors, Is.EqualTo(1));
                    Assert.That(Directory.GetFiles(directory), Is.Empty);
                }
                finally { ReleaseWhenCompleted(loading); }
            }
        }

        [UnityTest]
        public IEnumerator OneRetrySnapshotSucceedsAfterServerErrorEvenWhenConfigurationDisablesRetries()
        {
            var directory = Path.Combine(CreateTempDirectory(), "one-retry");
            var remote = new RemoteFileLoaderConfig
            {
                CacheDirectory = directory, MaxRetries = 1, RetryDelayMs = 25,
                MaxRetryDelayMs = 25, TimeoutSeconds = 0
            };
            var system = NewSystem(NewAsset(remote).ConvertToOptions());
            remote.MaxRetries = 0;
            remote.RetryDelayMs = 30000;
            yield return Await(system.InitializeAsync());
            using (var log = new DownloadFailureLogScope())
            using (var server = new LoopbackResponse("after-retry", 500, 200))
            {
                var loading = system.LoadAsync<string>(ResourceSource.Remote, server.Url);
                try
                {
                    yield return Await(loading);
                    Assert.That(loading.Result.Value, Is.EqualTo("after-retry"));
                    yield return Await(server.Served);
                    Assert.That(server.RequestCount, Is.EqualTo(2));
                    Assert.That(log.Errors, Is.Zero, "中间一次失败已通过重试恢复，不应报告最终失败。");
                    Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
                }
                finally { ReleaseWhenCompleted(loading); }
            }
        }

        [UnityTest]
        public IEnumerator ConfiguredConnectTimeoutEndsARequestWithNoResponseHeaders()
            => AssertStalledDownloadTimesOut(waitForBody: false);

        [UnityTest]
        public IEnumerator ConfiguredReadTimeoutEndsARequestWithHeadersButNoBody()
            => AssertStalledDownloadTimesOut(waitForBody: true);

        private IEnumerator AssertStalledDownloadTimesOut(bool waitForBody)
        {
            var directory = Path.Combine(CreateTempDirectory(), waitForBody ? "read-timeout" : "connect-timeout");
            var remote = new RemoteFileLoaderConfig
            {
                CacheDirectory = directory, MaxRetries = 0, TimeoutSeconds = 0,
                ConnectTimeoutMs = waitForBody ? 6000 : 1000,
                ReadTimeoutMs = waitForBody ? 1000 : 6000
            };
            var system = NewSystem(NewAsset(remote).ConvertToOptions());
            yield return Await(system.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            using (var log = new DownloadFailureLogScope())
            using (var server = new LoopbackResponse("withheld-response", holdHeaders: !waitForBody, holdBody: waitForBody))
            {
                var loading = system.LoadAsync<string>(ResourceSource.Remote, server.Url, token: cancellation.Token);
                try
                {
                    yield return Await(waitForBody ? server.HeadersSent : server.FirstRequest);
                    var deadline = Time.realtimeSinceStartupAsDouble + 3;
                    while (!loading.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                    Assert.That(loading.IsCompleted, Is.True,
                        (waitForBody ? "ReadTimeoutMs" : "ConnectTimeoutMs") + "=1000 必须作用于实际异步下载，不能依赖资源总超时。");
                    var failure = Assert.Throws<ResourceException>(() => loading.GetAwaiter().GetResult());
                    Assert.That(failure.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.NetworkTimeout));
                    Assert.That(server.RequestCount, Is.EqualTo(1));
                    Assert.That(log.Errors, Is.EqualTo(1));
                    Assert.That(Directory.GetFiles(directory), Is.Empty);
                }
                finally
                {
                    cancellation.Cancel();
                    ReleaseWhenCompleted(loading);
                }
            }
        }

        private ResourceSystemConfig NewAsset(RemoteFileLoaderConfig remote)
        {
            var asset = TrackObject(ScriptableObject.CreateInstance<ResourceSystemConfig>());
            asset.Loaders.Add(remote);
            asset.Converters.Add(new TextConverterConfig());
            return asset;
        }

        private RS NewSystem(ResourceSystemOptions options)
        {
            var system = Track(new RS(options));
            _systems.Add(system);
            return system;
        }

        private FileCache OwnCache(FileCache cache)
        {
            _externalCaches.Add(cache);
            return cache;
        }

        private ResourceManager NewManager(RS system)
        {
            var manager = TrackObject(new GameObject(nameof(RemoteFileLoaderConfigPlayModeTests))).AddComponent<ResourceManager>();
            typeof(ResourceManager).GetField("_resSystem", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, system);
            return manager;
        }

        private static void ReleaseWhenCompleted<T>(Task<ResourceRef<T>> task)
        {
            task.ContinueWith(completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion) completed.Result.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private sealed class ReleaseRecordingLoader : ILoader
        {
            private readonly Action _release;
            public ReleaseRecordingLoader(Action release) { _release = release; }
            public Task<IResource> LoadAsync(ResourceKey key,
                IProgress<OperationProgress> progress, CancellationToken cancellation)
                => Task.FromResult<IResource>(new Product("value", _release));
        }

        /// <summary>仅监听本机随机端口；按指定状态码回应，或暂停响应头/正文以验证真实超时。</summary>
        private sealed class LoopbackResponse : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly TaskCompletionSource<bool> _firstRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _headersSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _continueResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly int[] _statusCodes;
            private readonly bool _holdHeaders;
            private readonly bool _holdBody;
            private int _requestCount;
            public string Url { get; }
            public Task<string> Served { get; }
            public Task FirstRequest => _firstRequest.Task;
            public Task HeadersSent => _headersSent.Task;
            public int RequestCount => Volatile.Read(ref _requestCount);

            public LoopbackResponse(string content, params int[] statusCodes)
                : this(content, statusCodes.Length == 0 ? new[] { 200 } : statusCodes, false, false) { }

            public LoopbackResponse(string content, bool holdHeaders, bool holdBody)
                : this(content, new[] { 200 }, holdHeaders, holdBody) { }

            private LoopbackResponse(string content, int[] statusCodes, bool holdHeaders, bool holdBody)
            {
                _statusCodes = statusCodes;
                _holdHeaders = holdHeaders;
                _holdBody = holdBody;
                _listener.Start();
                Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/resource.txt";
                Served = ServeAsync(Encoding.UTF8.GetBytes(content));
            }

            private async Task<string> ServeAsync(byte[] body)
            {
                try
                {
                    string requestLine = null;
                    foreach (var status in _statusCodes)
                    {
                        using (var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false))
                        using (var stream = client.GetStream())
                        using (_stop.Token.Register(client.Close))
                        {
                            var header = new List<byte>();
                            var next = new byte[1];
                            while (header.Count < 8192)
                            {
                                var read = await stream.ReadAsync(next, 0, 1, _stop.Token).ConfigureAwait(false);
                                if (read == 0) throw new EndOfStreamException("Incomplete local HTTP request.");
                                header.Add(next[0]);
                                var count = header.Count;
                                if (count >= 4 && header[count - 4] == '\r' && header[count - 3] == '\n'
                                    && header[count - 2] == '\r' && header[count - 1] == '\n') break;
                            }
                            Interlocked.Increment(ref _requestCount);
                            _firstRequest.TrySetResult(true);
                            if (_holdHeaders) await WaitForResponseRelease().ConfigureAwait(false);
                            var response = Encoding.ASCII.GetBytes("HTTP/1.1 " + status
                                + (status == 200 ? " OK" : " Internal Server Error")
                                + "\r\nContent-Length: " + body.Length
                                + "\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(response, 0, response.Length, _stop.Token).ConfigureAwait(false);
                            _headersSent.TrySetResult(true);
                            if (_holdBody) await WaitForResponseRelease().ConfigureAwait(false);
                            await stream.WriteAsync(body, 0, body.Length, _stop.Token).ConfigureAwait(false);
                            requestLine = Encoding.ASCII.GetString(header.ToArray()).Split('\n')[0].TrimEnd('\r');
                        }
                    }
                    return requestLine;
                }
                catch (Exception) when (_stop.IsCancellationRequested) { return null; }
            }

            private async Task WaitForResponseRelease()
            {
                using (_stop.Token.Register(() => _continueResponse.TrySetCanceled()))
                    await _continueResponse.Task.ConfigureAwait(false);
            }

            public void Dispose()
            {
                if (_stop.IsCancellationRequested) return;
                _stop.Cancel();
                _listener.Stop();
            }
        }

        /// <summary>只接收测试预期的下载最终失败日志，其余日志继续交给原记录器。</summary>
        private sealed class DownloadFailureLogScope : ILog, IDisposable
        {
            private readonly ILog _previous;
            private int _errors;
            public int Errors => Volatile.Read(ref _errors);

            public DownloadFailureLogScope()
            {
                _previous = (ILog)typeof(Log).GetField("_log", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                Log.SetLog(this);
            }

            public void Dispose() => Log.SetLog(_previous);
            public void Info(string message) => _previous?.Info(message);
            public void Info(string message, object context) => _previous?.Info(message, context);
            public void Debug(string message) => _previous?.Debug(message);
            public void Debug(string message, object context) => _previous?.Debug(message, context);
            public void Warn(string message) => _previous?.Warn(message);
            public void Warn(string message, object context) => _previous?.Warn(message, context);
            public void Error(string message)
            {
                if (message.StartsWith("[DownloadTask] 下载失败", StringComparison.Ordinal)) Interlocked.Increment(ref _errors);
                else _previous?.Error(message);
            }
            public void Error(string message, object context)
            {
                if (message.StartsWith("[DownloadTask] 下载失败", StringComparison.Ordinal)) Interlocked.Increment(ref _errors);
                else _previous?.Error(message, context);
            }
            public void Assert(bool condition, string message) => _previous?.Assert(condition, message);
        }
    }
}
