using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ToolKit.Tools.Common;
using UnityEngine.TestTools;
using UnityToolKit.Runtime.ResourceSystem;
using ResourceSystemCore = ToolKit.Tools.Common.ResourceSystem;

namespace Tests.PlayMode.ResourceSystem
{
    /// <summary> 实际文件 IO、文本转换和远程缓存的完整加载链路；下载由可控 Fetch 提供，不依赖网络。 </summary>
    public class ResourceFilePlayModeTests : ResourcePlayModeTestBase
    {
        private const string RemoteAddress = "https://resource-system-tests.invalid/content.txt?v=1";
        private readonly List<ResourceSystemCore> _fileSystems = new List<ResourceSystemCore>();
        private readonly List<FileCache> _caches = new List<FileCache>();

        [UnityTearDown]
        public IEnumerator StopFileCaches()
        {
            // 资源系统只借用 FileCache。先结束加载，再由创建者关闭缓存，最后基类删除测试目录。
            foreach (var system in _fileSystems) yield return Await(system.DisposeAsync().AsTask());
            foreach (var cache in _caches) yield return Await(cache.StopAsync());
            _fileSystems.Clear();
            _caches.Clear();
        }

        [UnityTest]
        public IEnumerator LocalFileLoadsRelativeAndAbsolutePathsAsBytesAndUtf8Text()
        {
            var directory = CreateTempDirectory();
            var path = Path.Combine(directory, "中文内容.txt");
            const string content = "本地资源 / PlayMode / \u2603";
            var expected = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(path, expected);
            var system = CreateLocalSystem(directory);
            yield return Await(system.InitializeAsync());

            var bytes = system.LoadAsync<byte[]>(ResourceSource.Local, "中文内容.txt");
            var text = system.LoadAsync<string>(ResourceSource.Local, path);
            yield return Await(Task.WhenAll(bytes, text));
            using (var bytesRef = bytes.Result)
            using (var textRef = text.Result)
            {
                CollectionAssert.AreEqual(expected, bytesRef.Value);
                Assert.That(textRef.Value, Is.EqualTo(content));
            }
        }

        [UnityTest]
        public IEnumerator EmptyLocalFileIsAValidEmptyResource()
        {
            var directory = CreateTempDirectory();
            File.WriteAllBytes(Path.Combine(directory, "empty.txt"), Array.Empty<byte>());
            var system = CreateLocalSystem(directory);
            yield return Await(system.InitializeAsync());

            var bytes = system.LoadAsync<byte[]>(ResourceSource.Local, "empty.txt");
            var text = system.LoadAsync<string>(ResourceSource.Local, "empty.txt");
            yield return Await(Task.WhenAll(bytes, text));
            using (var bytesRef = bytes.Result)
            using (var textRef = text.Result)
            {
                Assert.That(bytesRef.Value, Is.Empty);
                Assert.That(textRef.Value, Is.Empty);
            }
        }

        [UnityTest]
        public IEnumerator MissingFileAndInvalidUtf8KeepTheirDiagnosticStage()
        {
            var directory = CreateTempDirectory();
            File.WriteAllBytes(Path.Combine(directory, "invalid.txt"), new byte[] { 0xc3, 0x28 });
            var system = CreateLocalSystem(directory);
            yield return Await(system.InitializeAsync());

            var missing = system.LoadAsync<byte[]>(ResourceSource.Local, "missing.txt");
            yield return Finish(missing);
            var missingError = Assert.Throws<ResourceException>(() => missing.GetAwaiter().GetResult());
            Assert.That(missingError.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.FileNotFound));
            Assert.That(missingError.ResourceError.Stage, Is.EqualTo(LoadStage.ReadFile));

            var invalid = system.LoadAsync<string>(ResourceSource.Local, "invalid.txt");
            yield return Finish(invalid);
            var conversionError = Assert.Throws<ResourceException>(() => invalid.GetAwaiter().GetResult());
            Assert.That(conversionError.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.AssetConversionFailed));
            Assert.That(conversionError.ResourceError.Stage, Is.EqualTo(LoadStage.Convert));
            Assert.That(conversionError.InnerException, Is.TypeOf<DecoderFallbackException>());
            Assert.That(system.Snapshot().Entries, Is.Empty, "失败的文件和转换不能登记为可用资源。");
        }

        [UnityTest]
        public IEnumerator RemoteRepresentationsShareOneFetchAndCancelIndependently()
        {
            var directory = CreateTempDirectory();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fetchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fetchCount = 0;
            var fillToken = CancellationToken.None;
            var cache = CreateCache(directory, async (_, path, progress, token) =>
            {
                Interlocked.Increment(ref fetchCount);
                fillToken = token;
                fetchStarted.TrySetResult(true);
                using (token.Register(() => ready.TrySetCanceled(token))) await ready.Task.ConfigureAwait(false);
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes("共享文件"));
            });
            var observed = new ObservedRemoteLoader(cache);
            var options = RemoteOptions(cache).AddLoader(ResourceSource.Remote, observed, new LoaderPolicy(2, null));
            var system = CreateSystem(options);
            yield return Await(system.InitializeAsync());

            using (var cancelBytes = new CancellationTokenSource())
            {
                var bytes = system.LoadAsync<byte[]>(ResourceSource.Remote, RemoteAddress, token: cancelBytes.Token);
                var text = system.LoadAsync<string>(ResourceSource.Remote, RemoteAddress);
                yield return Await(fetchStarted.Task);
                // 两个 RemoteFileLoader 都已调用缓存，保证取消发生在双方加入填充之后。
                yield return Until(() => observed.Started == 2);
                Assert.That(Volatile.Read(ref fetchCount), Is.EqualTo(1));
                cancelBytes.Cancel();
                yield return Finish(bytes);
                Assert.Catch<OperationCanceledException>(() => bytes.GetAwaiter().GetResult());
                Assert.That(fillToken.IsCancellationRequested, Is.False);

                ready.TrySetResult(true);
                yield return Await(text);
                using (var reference = text.Result) Assert.That(reference.Value, Is.EqualTo("共享文件"));
                Assert.That(Volatile.Read(ref fetchCount), Is.EqualTo(1));
                Assert.That(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories), Is.Empty);
                Assert.That(File.ReadAllText(new HashedPathConverter(directory).RemoteUrlToLocalPath(RemoteAddress)),
                    Is.EqualTo("共享文件"));
            }
        }

        [UnityTest]
        public IEnumerator InitializationRemovesOrphansAndOldestFilesBeforeLoading()
        {
            var directory = CreateTempDirectory();
            PutFile(directory, "old.bin", 100, 30);
            PutFile(directory, "middle.bin", 100, 20);
            PutFile(directory, "new.bin", 100, 10);
            Directory.CreateDirectory(Path.Combine(directory, "nested"));
            File.WriteAllText(Path.Combine(directory, "nested", "unfinished.tmp"), "interrupted");
            var fetchCount = 0;
            var cache = CreateCache(directory, (_, path, progress, token) =>
            {
                Interlocked.Increment(ref fetchCount);
                File.WriteAllText(path, "downloaded");
                return Task.CompletedTask;
            }, startupTargetBytes: 250);
            var system = CreateSystem(RemoteOptions(cache));

            yield return Await(system.InitializeAsync());

            Assert.That(system.IsInitialized, Is.True);
            var remaining = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName).OrderBy(name => name).ToArray();
            CollectionAssert.AreEqual(new[] { "middle.bin", "new.bin" }, remaining);
            Assert.That(Volatile.Read(ref fetchCount), Is.Zero, "初始化只清理，不提前下载。");
            var request = system.LoadAsync<string>(ResourceSource.Remote, RemoteAddress);
            yield return Await(request);
            using (var reference = request.Result) Assert.That(reference.Value, Is.EqualTo("downloaded"));
        }

        [UnityTest]
        public IEnumerator ExpiredDiskFileRefreshesAfterMemoryResourceIsReleasedAndUnloaded()
        {
            var directory = CreateTempDirectory();
            var fetchCount = 0;
            var cache = CreateCache(directory, (_, path, progress, token) =>
            {
                var version = Interlocked.Increment(ref fetchCount);
                File.WriteAllText(path, "version-" + version, new UTF8Encoding(false));
                return Task.CompletedTask;
            }, maxAge: TimeSpan.FromMinutes(10));
            var system = CreateSystem(RemoteOptions(cache));
            yield return Await(system.InitializeAsync());
            var first = system.LoadAsync<string>(ResourceSource.Remote, RemoteAddress);
            yield return Await(first);
            using (var reference = first.Result) Assert.That(reference.Value, Is.EqualTo("version-1"));
            var path = new HashedPathConverter(directory).RemoteUrlToLocalPath(RemoteAddress);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(20));

            var inMemory = system.LoadAsync<string>(ResourceSource.Remote, RemoteAddress);
            yield return Await(inMemory);
            using (var reference = inMemory.Result) Assert.That(reference.Value, Is.EqualTo("version-1"));
            Assert.That(Volatile.Read(ref fetchCount), Is.EqualTo(1), "TTL 不会主动刷新已缓存的内存资源。");

            yield return Await(system.UnloadAsync());
            var refreshed = system.LoadAsync<string>(ResourceSource.Remote, RemoteAddress);
            yield return Await(refreshed);
            using (var reference = refreshed.Result) Assert.That(reference.Value, Is.EqualTo("version-2"));
            Assert.That(Volatile.Read(ref fetchCount), Is.EqualTo(2));
            Assert.That(cache.IsFresh(path), Is.True);
            Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
        }

        [UnityTest]
        public IEnumerator NewSystemReusesPublishedDiskCacheWithoutFetchingAgain()
        {
            var directory = CreateTempDirectory();
            var fetchCount = 0;
            FileCache.Fetch fetch = (_, path, progress, token) =>
            {
                Interlocked.Increment(ref fetchCount);
                File.WriteAllText(path, "persistent-file", new UTF8Encoding(false));
                return Task.CompletedTask;
            };
            var firstCache = CreateCache(directory, fetch);
            var firstSystem = CreateSystem(RemoteOptions(firstCache));
            yield return Await(firstSystem.InitializeAsync());
            var first = firstSystem.LoadAsync<string>(ResourceSource.Remote, RemoteAddress);
            yield return Await(first);
            first.Result.Dispose();
            yield return Await(firstSystem.DisposeAsync().AsTask());
            yield return Await(firstCache.StopAsync());

            var secondCache = CreateCache(directory, fetch);
            var secondSystem = CreateSystem(RemoteOptions(secondCache));
            yield return Await(secondSystem.InitializeAsync());
            var second = secondSystem.LoadAsync<string>(ResourceSource.Remote, RemoteAddress);
            yield return Await(second);
            using (var reference = second.Result) Assert.That(reference.Value, Is.EqualTo("persistent-file"));
            Assert.That(Volatile.Read(ref fetchCount), Is.EqualTo(1));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator FailedFetchDeletesPartialFileAndNextRequestRetries()
        {
            var directory = CreateTempDirectory();
            var fetchCount = 0;
            var cache = CreateCache(directory, (_, path, progress, token) =>
            {
                var attempt = Interlocked.Increment(ref fetchCount);
                File.WriteAllText(path, attempt == 1 ? "partial" : "complete", new UTF8Encoding(false));
                return attempt == 1 ? Task.FromException(new IOException("test transfer interrupted")) : Task.CompletedTask;
            });
            var system = CreateSystem(RemoteOptions(cache));
            yield return Await(system.InitializeAsync());
            var failed = system.LoadAsync<string>(ResourceSource.Remote, RemoteAddress);
            yield return Finish(failed);
            var error = Assert.Throws<ResourceException>(() => failed.GetAwaiter().GetResult());
            Assert.That(error.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.FileIoFailed));
            Assert.That(error.ResourceError.Stage, Is.EqualTo(LoadStage.Download));
            Assert.That(error.ResourceError.Context["url"], Is.EqualTo(RemoteAddress));
            Assert.That(Directory.GetFiles(directory), Is.Empty);

            var retried = system.LoadAsync<string>(ResourceSource.Remote, RemoteAddress);
            yield return Await(retried);
            using (var reference = retried.Result) Assert.That(reference.Value, Is.EqualTo("complete"));
            Assert.That(Volatile.Read(ref fetchCount), Is.EqualTo(2));
            Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
        }

        [UnityTest]
        public IEnumerator CancelingTheOnlyRequestRemovesPartialDownloadAndAllowsRetry()
        {
            var directory = CreateTempDirectory();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fetchCount = 0;
            var cache = CreateCache(directory, async (_, path, progress, token) =>
            {
                if (Interlocked.Increment(ref fetchCount) == 1)
                {
                    File.WriteAllText(path, "partial");
                    started.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
                File.WriteAllText(path, "retried", new UTF8Encoding(false));
            });
            var system = CreateSystem(RemoteOptions(cache));
            yield return Await(system.InitializeAsync());

            using (var cancellation = new CancellationTokenSource())
            {
                var abandoned = system.LoadAsync<string>(ResourceSource.Remote, RemoteAddress, token: cancellation.Token);
                yield return Await(started.Task);
                Assert.That(Directory.GetFiles(directory, "*.tmp"), Has.Length.EqualTo(1));
                cancellation.Cancel();
                yield return Finish(abandoned);
                Assert.Catch<OperationCanceledException>(() => abandoned.GetAwaiter().GetResult());
                yield return Until(() => Directory.GetFiles(directory).Length == 0);

                var retried = system.LoadAsync<string>(ResourceSource.Remote, RemoteAddress);
                yield return Await(retried);
                using (var reference = retried.Result) Assert.That(reference.Value, Is.EqualTo("retried"));
                Assert.That(Volatile.Read(ref fetchCount), Is.EqualTo(2));
                Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
            }
        }

        [UnityTest]
        public IEnumerator CancelingDownloadAAllowsBToFinishBeforeUncooperativeAExits()
        {
            var directory = CreateTempDirectory();
            var paths = new HashedPathConverter(directory);
            var startedA = Signal();
            var canceledA = Signal();
            var finishA = Signal();
            var cache = CreateCache(directory, async (url, temporary, progress, token) =>
            {
                if (url == RemoteAddress)
                {
                    File.WriteAllText(temporary, "partial-a");
                    using (token.Register(() => canceledA.TrySetResult(true)))
                    {
                        startedA.TrySetResult(true);
                        await finishA.Task.ConfigureAwait(false); // 模拟不能立即停止的外部下载。
                    }
                    File.WriteAllText(temporary, "late-a");
                }
                else File.WriteAllText(temporary, "resource-b", new UTF8Encoding(false));
            });
            var options = RemoteOptions(cache).AddLoader(ResourceSource.Remote,
                new RemoteFileLoader(cache), new LoaderPolicy(1, null));
            var system = CreateSystem(options);
            yield return Await(system.InitializeAsync());

            using (var cancellation = new CancellationTokenSource())
            {
                Task<ResourceRef<string>> requestA = null;
                Task<ResourceRef<string>> requestB = null;
                try
                {
                    requestA = system.LoadAsync<string>(ResourceSource.Remote, RemoteAddress, token: cancellation.Token);
                    yield return Await(startedA.Task);
                    cancellation.Cancel();
                    yield return Finish(requestA);
                    Assert.Catch<OperationCanceledException>(() => requestA.GetAwaiter().GetResult());
                    yield return Await(canceledA.Task);

                    const string addressB = "https://resource-system-tests.invalid/b.txt";
                    requestB = system.LoadAsync<string>(ResourceSource.Remote, addressB);
                    yield return Await(requestB);
                    Assert.That(requestB.Result.Value, Is.EqualTo("resource-b"));
                    Assert.That(finishA.Task.IsCompleted, Is.False, "B 不必等待另一 URL 的外部填充真正停止。");
                    Assert.That(File.Exists(paths.RemoteUrlToLocalPath(RemoteAddress)), Is.False);

                    finishA.TrySetResult(true);
                    yield return Await(cache.StopAsync());
                    Assert.That(File.Exists(paths.RemoteUrlToLocalPath(RemoteAddress)), Is.False,
                        "已取消的 A 即使迟到写完，也不能发布为有效缓存。");
                    Assert.That(File.ReadAllText(paths.RemoteUrlToLocalPath(addressB)), Is.EqualTo("resource-b"));
                    Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
                }
                finally
                {
                    cancellation.Cancel();
                    finishA.TrySetResult(true);
                    ReleaseWhenCompleted(requestA);
                    ReleaseWhenCompleted(requestB);
                }
            }
        }

        [UnityTest]
        public IEnumerator SameUrlNewRepresentationWaitsForCanceledFillToExitBeforeStartingAgain()
        {
            var directory = CreateTempDirectory();
            var publishedPath = new HashedPathConverter(directory).RemoteUrlToLocalPath(RemoteAddress);
            var firstStarted = Signal();
            var firstCanceled = Signal();
            var finishFirst = Signal();
            var secondStarted = Signal();
            var finishSecond = Signal();
            var calls = 0;
            string firstTemporary = null;
            var cache = CreateCache(directory, async (_, temporary, progress, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstTemporary = temporary;
                    File.WriteAllText(temporary, "partial-old");
                    using (token.Register(() => firstCanceled.TrySetResult(true)))
                    {
                        firstStarted.TrySetResult(true);
                        await finishFirst.Task.ConfigureAwait(false);
                    }
                    File.WriteAllText(temporary, "late-old");
                }
                else
                {
                    secondStarted.TrySetResult(true);
                    using (token.Register(() => finishSecond.TrySetCanceled(token)))
                        await finishSecond.Task.ConfigureAwait(false);
                    File.WriteAllText(temporary, "fresh-new", new UTF8Encoding(false));
                }
            });
            var observed = new ObservedRemoteLoader(cache);
            var system = CreateSystem(RemoteOptions(cache)
                .AddLoader(ResourceSource.Remote, observed, new LoaderPolicy(2, null)));
            yield return Await(system.InitializeAsync());

            using (var cancellation = new CancellationTokenSource())
            {
                Task<ResourceRef<byte[]>> first = null;
                Task<ResourceRef<string>> second = null;
                try
                {
                    first = system.LoadAsync<byte[]>(ResourceSource.Remote, RemoteAddress, token: cancellation.Token);
                    yield return Await(firstStarted.Task);
                    cancellation.Cancel();
                    yield return Finish(first);
                    Assert.Catch<OperationCanceledException>(() => first.GetAwaiter().GetResult());
                    yield return Await(firstCanceled.Task);

                    // 目标类型不同，绕过核心的同 ResourceKey 合并，直接验证 FileCache 的同 URL 交接。
                    second = system.LoadAsync<string>(ResourceSource.Remote, RemoteAddress);
                    yield return Until(() => observed.Started == 2);
                    Assert.That(Volatile.Read(ref calls), Is.EqualTo(1));
                    Assert.That(second.IsCompleted, Is.False);
                    Assert.That(secondStarted.Task.IsCompleted, Is.False);

                    finishFirst.TrySetResult(true);
                    yield return Await(secondStarted.Task);
                    Assert.That(Volatile.Read(ref calls), Is.EqualTo(2));
                    Assert.That(File.Exists(firstTemporary), Is.False, "新填充开始前，旧轮临时文件必须已清理。");
                    Assert.That(File.Exists(publishedPath), Is.False, "旧轮迟到成功不得短暂发布为新请求的结果。");

                    finishSecond.TrySetResult(true);
                    yield return Await(second);
                    Assert.That(second.Result.Value, Is.EqualTo("fresh-new"));
                    Assert.That(File.ReadAllText(publishedPath), Is.EqualTo("fresh-new"));
                    Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
                }
                finally
                {
                    cancellation.Cancel();
                    finishFirst.TrySetResult(true);
                    finishSecond.TrySetResult(true);
                    ReleaseWhenCompleted(first);
                    ReleaseWhenCompleted(second);
                }
            }
        }

        [UnityTest]
        public IEnumerator DisposingOneSystemDoesNotCloseTheCacheBorrowedByAnotherSystem()
        {
            var directory = CreateTempDirectory();
            var fetchStarted = Signal();
            var finishFetch = Signal();
            var calls = 0;
            var fillToken = CancellationToken.None;
            var cache = CreateCache(directory, async (_, temporary, progress, token) =>
            {
                Interlocked.Increment(ref calls);
                fillToken = token;
                fetchStarted.TrySetResult(true);
                using (token.Register(() => finishFetch.TrySetCanceled(token)))
                    await finishFetch.Task.ConfigureAwait(false);
                File.WriteAllText(temporary, "shared-cache", new UTF8Encoding(false));
            });
            // 缓存由外部 owner 先初始化一次；两个系统只借用，不能在加载中重复执行启动清理。
            yield return Await(cache.InitializeAsync());
            var firstLoader = new ObservedRemoteLoader(cache);
            var secondLoader = new ObservedRemoteLoader(cache);
            var firstSystem = CreateSystem(BorrowedRemoteOptions(firstLoader));
            var secondSystem = CreateSystem(BorrowedRemoteOptions(secondLoader));
            yield return Await(Task.WhenAll(firstSystem.InitializeAsync(), secondSystem.InitializeAsync()));
            Task<ResourceRef<string>> first = null;
            Task<ResourceRef<string>> second = null;
            Task<ResourceRef<string>> later = null;
            try
            {
                first = firstSystem.LoadAsync<string>(ResourceSource.Remote, RemoteAddress);
                second = secondSystem.LoadAsync<string>(ResourceSource.Remote, RemoteAddress);
                yield return Await(fetchStarted.Task);
                yield return Until(() => firstLoader.Started == 1 && secondLoader.Started == 1);
                Assert.That(Volatile.Read(ref calls), Is.EqualTo(1));

                yield return Await(firstSystem.DisposeAsync().AsTask());
                yield return Finish(first);
                Assert.Catch<OperationCanceledException>(() => first.GetAwaiter().GetResult());
                Assert.That(fillToken.IsCancellationRequested, Is.False);
                Assert.That(second.IsCompleted, Is.False);
                finishFetch.TrySetResult(true);
                yield return Await(second);
                Assert.That(second.Result.Value, Is.EqualTo("shared-cache"));

                later = secondSystem.LoadAsync<string>(ResourceSource.Remote,
                    "https://resource-system-tests.invalid/still-open.txt");
                yield return Await(later);
                Assert.That(later.Result.Value, Is.EqualTo("shared-cache"));
                Assert.That(Volatile.Read(ref calls), Is.EqualTo(2), "共享缓存仍然接收其它系统的新填充。");
            }
            finally
            {
                finishFetch.TrySetResult(true);
                ReleaseWhenCompleted(first);
                ReleaseWhenCompleted(second);
                ReleaseWhenCompleted(later);
            }
        }

        [UnityTest]
        public IEnumerator CacheOwnerStopWaitsForFillExitCancelsWaitersAndRemovesPartialFiles()
        {
            var directory = CreateTempDirectory();
            var started = Signal();
            var canceled = Signal();
            var finish = Signal();
            var cache = CreateCache(directory, async (_, temporary, progress, token) =>
            {
                File.WriteAllText(temporary, "partial");
                using (token.Register(() => canceled.TrySetResult(true)))
                {
                    started.TrySetResult(true);
                    await finish.Task.ConfigureAwait(false);
                }
                File.WriteAllText(temporary, "late-success");
            });
            yield return Await(cache.InitializeAsync());
            var request = cache.GetFileAsync(RemoteAddress, null, CancellationToken.None);
            try
            {
                yield return Await(started.Task);
                var stopping = cache.StopAsync();
                yield return Await(canceled.Task);
                Assert.That(stopping.IsCompleted, Is.False, "StopAsync 必须等待真实填充退出，不能遗留后台写入。");
                Assert.That(request.IsCompleted, Is.False);
                finish.TrySetResult(true);
                yield return Await(stopping);
                yield return Finish(request);
                Assert.Catch<OperationCanceledException>(() => request.GetAwaiter().GetResult());
                Assert.That(Directory.GetFiles(directory), Is.Empty, "取消填充不发布文件，也不残留临时文件。");

                var rejected = cache.GetFileAsync("https://resource-system-tests.invalid/new.txt", null, CancellationToken.None);
                yield return Finish(rejected);
                Assert.Throws<InvalidOperationException>(() => rejected.GetAwaiter().GetResult());
            }
            finally { finish.TrySetResult(true); }
        }

        private static TaskCompletionSource<bool> Signal()
            => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static void ReleaseWhenCompleted<T>(Task<ResourceRef<T>> task)
        {
            if (task == null) return;
            // 即使断言失败时请求仍在途，成功迟到的引用也有人归还。
            task.ContinueWith(completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion) completed.Result.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static ResourceSystemOptions BorrowedRemoteOptions(ILoader loader)
        {
            return new ResourceSystemOptions { ExecutionContext = new UnityExecutionContext() }
                .AddLoader(ResourceSource.Remote, loader, new LoaderPolicy(2, null))
                .AddConverter<string>(new TextConverter());
        }

        private ResourceSystemCore CreateLocalSystem(string directory)
        {
            return CreateSystem(new ResourceSystemOptions { ExecutionContext = new UnityExecutionContext() }
                .AddLoader(ResourceSource.Local, new LocalFileLoader(directory), new LoaderPolicy(2, null))
                .AddConverter<string>(new TextConverter()));
        }

        private ResourceSystemCore CreateSystem(ResourceSystemOptions options)
        {
            var system = Track(new ResourceSystemCore(options));
            _fileSystems.Add(system);
            return system;
        }

        private FileCache CreateCache(string directory, FileCache.Fetch fetch,
            long startupTargetBytes = long.MaxValue, TimeSpan? maxAge = null)
        {
            var cache = new FileCache(new HashedPathConverter(directory), fetch, startupTargetBytes, maxAge);
            _caches.Add(cache);
            return cache;
        }

        private static ResourceSystemOptions RemoteOptions(FileCache cache)
        {
            return RemoteFileLoader.AddRemoteFiles(
                    new ResourceSystemOptions { ExecutionContext = new UnityExecutionContext() }, cache,
                    new LoaderPolicy(4, null))
                .AddConverter<string>(new TextConverter());
        }

        private static void PutFile(string directory, string name, int size, int ageMinutes)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, new byte[size]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(ageMinutes));
        }

        private sealed class ObservedRemoteLoader : ILoader
        {
            private readonly RemoteFileLoader _inner;
            private int _started;

            public ObservedRemoteLoader(FileCache cache) { _inner = new RemoteFileLoader(cache); }
            public int Started => Volatile.Read(ref _started);

            public Task<IResource> LoadAsync(ResourceKey key, IExecutionContext context,
                IProgress<OperationProgress> progress, CancellationToken cancellation)
            {
                var task = _inner.LoadAsync(key, context, progress, cancellation);
                Interlocked.Increment(ref _started);
                return task;
            }
        }
    }
}
