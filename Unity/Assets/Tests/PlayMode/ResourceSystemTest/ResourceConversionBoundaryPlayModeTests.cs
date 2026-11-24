using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ToolKit.Tools.Common;
using UnityEngine.TestTools;
using UnityToolKit.Runtime.ResourceSystem;
using RS = ToolKit.Tools.Common.ResourceSystem;

namespace Tests.PlayMode.ResourceSystem
{
    public class ResourceConversionBoundaryPlayModeTests : ResourcePlayModeTestBase
    {
        private readonly List<Task<ResourceRef<string>>> _requests = new List<Task<ResourceRef<string>>>();

        [UnityTearDown]
        public IEnumerator ReturnCompletedReferences()
        {
            // 各用例的 finally 已放行转换门闩；失败后成功迟到的引用也必须先归还。
            var pending = Task.WhenAll(_requests);
            yield return Finish(pending);
            foreach (var request in _requests)
            {
                if (request.Status == TaskStatus.RanToCompletion) request.Result.Dispose();
                else if (request.IsFaulted) _ = request.Exception;
            }
            _ = pending.Exception;
            _requests.Clear();
        }

        [UnityTest]
        public IEnumerator CancelDuringConversionThenLoadBReleasesBothLateAProducts()
        {
            var input = new OwnedProduct(new byte[] { 1 });
            var output = new OwnedProduct("converted-A");
            var converter = new PausedConverter();
            var system = CreateSystem(input, converter);
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            yield return Await(system.InitializeAsync());
            using (var cancelA = new CancellationTokenSource())
            {
                try
                {
                    var a = Load(system, "A", cancelA.Token);
                    yield return Await(converter.Started.Task);
                    cancelA.Cancel();
                    var b = Load(system, "B");
                    yield return Finish(a);
                    yield return Await(b);
                    using (var reference = b.Result) Assert.That(reference.Value, Is.EqualTo("B"));
                    Assert.That(a.IsCanceled, Is.True);
                    Assert.That(converter.Token.IsCancellationRequested, Is.True);
                    Assert.That(input.Releases, Is.Zero, "转换尚在使用输入，取消等待不能提前释放它。");

                    converter.Complete(output);
                    yield return Until(() => output.Releases == 1);
                    Assert.That(input.Releases, Is.EqualTo(1));
                    Assert.That(input.ReleaseThread, Is.EqualTo(mainThread));
                    Assert.That(output.ReleaseThread, Is.EqualTo(mainThread));
                    Assert.That(system.Snapshot().Entries.Any(entry => entry.Key.Address == "A"), Is.False);
                    yield return Await(system.UnloadAsync());
                    Assert.That(output.Releases, Is.EqualTo(1));
                }
                finally { converter.Complete(output); }
            }
        }

        [UnityTest]
        public IEnumerator ConverterObservingCancellationReleasesInputAndAllowsNextRequest()
        {
            var input = new OwnedProduct(new byte[] { 1 });
            var converter = new PausedConverter(observeCancellation: true);
            var system = CreateSystem(input, converter, concurrency: 1);
            yield return Await(system.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    var a = Load(system, "A", cancellation.Token);
                    yield return Await(converter.Started.Task);
                    cancellation.Cancel();
                    var b = Load(system, "B");
                    yield return Finish(a);
                    yield return Await(b);
                    using (var reference = b.Result) Assert.That(reference.Value, Is.EqualTo("B"));
                    Assert.That(a.IsCanceled, Is.True);
                    Assert.That(input.Releases, Is.EqualTo(1));
                    Assert.That(system.Snapshot().Entries.Any(entry => entry.Key.Address == "A"), Is.False);
                    Assert.That(system.Snapshot().Residues, Is.Empty);
                }
                finally { converter.Cancel(); }
            }
        }

        [UnityTest]
        public IEnumerator CancelingOneConversionWaiterDoesNotReleaseTheSurvivorsOutput()
        {
            var input = new OwnedProduct(new byte[] { 1 });
            var output = new OwnedProduct("shared-output");
            var converter = new PausedConverter();
            var system = CreateSystem(input, converter);
            yield return Await(system.InitializeAsync());
            using (var firstCancellation = new CancellationTokenSource())
            using (var survivorCancellation = new CancellationTokenSource())
            {
                ResourceRef<string> held = null;
                try
                {
                    var first = Load(system, "A", firstCancellation.Token);
                    yield return Await(converter.Started.Task);
                    var survivor = Load(system, "A", survivorCancellation.Token);
                    firstCancellation.Cancel();
                    yield return Finish(first);
                    Assert.That(first.IsCanceled, Is.True);
                    Assert.That(converter.Token.IsCancellationRequested, Is.False);
                    converter.Complete(output);
                    yield return Await(survivor);
                    held = survivor.Result;
                    survivorCancellation.Cancel(); // 已交付的引用仍由业务持有，取消不能反向撤销它。
                    Assert.That(held.Value, Is.EqualTo("shared-output"));
                    Assert.That(input.Releases, Is.EqualTo(1));
                    Assert.That(output.Releases, Is.Zero);
                    var inUse = system.UnloadAsync();
                    yield return Await(inUse);
                    Assert.That(inUse.Result.SkippedInUse.Count, Is.EqualTo(1));
                    held.Dispose();
                    yield return Await(system.UnloadAsync());
                    Assert.That(output.Releases, Is.EqualTo(1));
                }
                finally { held?.Dispose(); converter.Complete(output); }
            }
        }

        [UnityTest]
        public IEnumerator DisposeDuringConversionWaitsForLateOutputAndReleasesInputAndOutput()
        {
            var input = new OwnedProduct(new byte[] { 1 });
            var output = new OwnedProduct("late-output");
            var converter = new PausedConverter();
            var system = CreateSystem(input, converter);
            yield return Await(system.InitializeAsync());
            try
            {
                var request = Load(system, "A");
                yield return Await(converter.Started.Task);
                var disposal = system.DisposeAsync().AsTask();
                yield return Until(() => converter.Token.IsCancellationRequested);
                Assert.That(disposal.IsCompleted, Is.False, "后端仍使用输入，系统退出必须等待实际清理完成。");
                Assert.That(input.Releases, Is.Zero);
                converter.Complete(output);
                yield return Await(disposal);
                yield return Finish(request);
                Assert.That(request.IsCanceled, Is.True);
                Assert.That(input.Releases, Is.EqualTo(1));
                Assert.That(output.Releases, Is.EqualTo(1));
                Assert.That(system.Snapshot().Entries, Is.Empty);
                Assert.That(system.Snapshot().Residues, Is.Empty);
            }
            finally { converter.Complete(output); }
        }

        [UnityTest]
        public IEnumerator InputReleaseFailureRemainsOwnedWithoutFailingTheConvertedResult()
        {
            var failure = new InvalidOperationException("input release failed");
            var input = new OwnedProduct(new byte[] { 1 }) { ReleaseError = failure };
            var output = new OwnedProduct("usable-output");
            var converter = new PausedConverter();
            var system = CreateSystem(input, converter);
            yield return Await(system.InitializeAsync());
            try
            {
                var request = Load(system, "A");
                yield return Await(converter.Started.Task);
                converter.Complete(output);
                yield return Await(request);
                using (var reference = request.Result)
                {
                    Assert.That(reference.Value, Is.EqualTo("usable-output"));
                    Assert.That(input.Releases, Is.EqualTo(1));
                    Assert.That(system.Snapshot().Residues.Single().Error, Is.SameAs(failure));
                    Assert.That(output.Releases, Is.Zero);
                    input.ReleaseError = null;
                    var unload = system.UnloadAsync();
                    yield return Await(unload);
                    Assert.That(unload.Result.Released.Count, Is.EqualTo(1), "只重试未被持有的中间产物。");
                    Assert.That(unload.Result.SkippedInUse.Count, Is.EqualTo(1));
                    Assert.That(input.Releases, Is.EqualTo(2));
                    Assert.That(output.Releases, Is.Zero);
                }
                yield return Await(system.UnloadAsync());
                Assert.That(output.Releases, Is.EqualTo(1));
                Assert.That(system.Snapshot().Residues, Is.Empty);
            }
            finally { input.ReleaseError = null; converter.Complete(output); }
        }

        [UnityTest]
        public IEnumerator InvalidLoaderProductWithNullValueStillReturnsItsReleaseOwnership()
        {
            // 自定义 loader 返回了有释放责任、但 Value 无效的产物。
            // 拒绝交付不应让已转移给核心的释放责任凭空消失。
            var invalid = new OwnedProduct(null);
            var system = CreateSystem(invalid);
            yield return Await(system.InitializeAsync());
            var request = Load(system, "A");
            yield return Finish(request);
            var failure = Assert.Throws<ResourceException>(() => request.GetAwaiter().GetResult());
            Assert.That(failure.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.LoaderInvalidResult));
            yield return Await(system.DisposeAsync().AsTask());
            Assert.That(invalid.Releases, Is.EqualTo(1), "被拒绝的非空 IResource 仍必须释放一次。");
            Assert.That(system.Snapshot().Entries, Is.Empty);
            Assert.That(system.Snapshot().Residues, Is.Empty);
        }

        [UnityTest]
        public IEnumerator InvalidLoaderProductReleaseFailureIsRetainedAndCanBeRetriedInContext()
        {
            var releaseFailure = new InvalidOperationException("invalid product release failed");
            var invalid = new OwnedProduct(null) { ReleaseError = releaseFailure };
            var system = CreateSystem(invalid);
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            yield return Await(system.InitializeAsync());
            try
            {
                var request = Load(system, "A");
                yield return Finish(request);
                var failure = Assert.Throws<ResourceException>(() => request.GetAwaiter().GetResult());
                Assert.That(failure.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.LoaderInvalidResult),
                    "清理失败不能取代原先的无效产物诊断。");
                Assert.That(invalid.Releases, Is.EqualTo(1));
                Assert.That(invalid.ReleaseThread, Is.EqualTo(mainThread));
                var snapshot = system.Snapshot();
                Assert.That(snapshot.Entries, Is.Empty);
                Assert.That(snapshot.Residues.Count, Is.EqualTo(1));
                Assert.That(snapshot.Residues[0].Error, Is.SameAs(releaseFailure));
                Assert.That(snapshot.Residues[0].Key.Address, Is.EqualTo("A"));

                invalid.ReleaseError = null;
                var retried = system.UnloadAsync();
                yield return Await(retried);
                Assert.That(retried.Result.Released.Count, Is.EqualTo(1));
                Assert.That(retried.Result.Failed, Is.Empty);
                Assert.That(invalid.Releases, Is.EqualTo(2));
                Assert.That(invalid.ReleaseThread, Is.EqualTo(mainThread));
                Assert.That(system.Snapshot().Residues, Is.Empty);
                yield return Await(system.UnloadAsync());
                Assert.That(invalid.Releases, Is.EqualTo(2), "成功重试后不能再次取得同一产物的释放责任。");
            }
            finally { invalid.ReleaseError = null; }
        }

        [UnityTest]
        public IEnumerator ConcurrentReferenceDisposeAndUnloadReleaseEachProductOnlyOnce()
        {
            var product = new OwnedProduct("shared");
            var system = CreateSystem(product);
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            yield return Await(system.InitializeAsync());
            var loading = Load(system, "A");
            yield return Await(loading);
            using (var original = loading.Result)
            using (var retained = original.Retain())
            {
                var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var disposing = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
                {
                    await start.Task;
                    original.Dispose();
                })).ToArray();
                start.SetResult(true);
                yield return Await(Task.WhenAll(disposing));
                Assert.That(retained.Value, Is.EqualTo("shared"));
                Assert.That(system.Snapshot().Entries.Single().Holds, Is.EqualTo(1));
                var stillHeld = system.UnloadAsync();
                yield return Await(stillHeld);
                Assert.That(stillHeld.Result.SkippedInUse.Count, Is.EqualTo(1));
                Assert.That(product.Releases, Is.Zero);
            }
            var unloadStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var unloads = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await unloadStart.Task;
                return await system.UnloadAsync();
            })).ToArray();
            unloadStart.SetResult(true);
            yield return Await(Task.WhenAll(unloads));
            Assert.That(unloads.Sum(task => task.Result.Released.Count), Is.EqualTo(1));
            Assert.That(product.Releases, Is.EqualTo(1));
            Assert.That(product.ReleaseThread, Is.EqualTo(mainThread));
            Assert.That(system.Snapshot().Entries, Is.Empty);
        }

        private Task<ResourceRef<string>> Load(RS system, string address, CancellationToken token = default)
        {
            var task = system.LoadAsync<string>(ResourceSource.Local, address, token: token);
            _requests.Add(task);
            return task;
        }

        private RS CreateSystem(IResource a, IProductConverter converter = null, int concurrency = 2)
        {
            var options = new ResourceSystemOptions { ExecutionContext = new UnityExecutionContext() }
                .AddLoader(ResourceSource.Local, new ImmediateLoader(a), new LoaderPolicy(concurrency, null));
            if (converter != null) options.AddConverter<string>(converter);
            return Track(new RS(options));
        }

        private sealed class ImmediateLoader : ILoader
        {
            private readonly IResource _a;
            public ImmediateLoader(IResource a) { _a = a; }
            public Task<IResource> LoadAsync(ResourceKey key,
                IProgress<OperationProgress> progress, CancellationToken token)
                => Task.FromResult(key.Address == "A" ? _a : new Product(key.Address));
        }

        private sealed class PausedConverter : IProductConverter
        {
            private readonly bool _observeCancellation;
            private readonly TaskCompletionSource<IResource> _completion = new TaskCompletionSource<IResource>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationToken Token { get; private set; }
            public PausedConverter(bool observeCancellation = false) { _observeCancellation = observeCancellation; }
            public async Task<IResource> ConvertAsync(IResource source, ResourceKey key, CancellationToken token)
            {
                Token = token;
                using (token.Register(() => { if (_observeCancellation) _completion.TrySetCanceled(token); }))
                {
                    Started.TrySetResult(true);
                    return await _completion.Task;
                }
            }
            public void Complete(IResource product) => _completion.TrySetResult(product);
            public void Cancel() => _completion.TrySetCanceled();
        }

        private sealed class OwnedProduct : IResource
        {
            private int _releases;
            public OwnedProduct(object value) { Value = value; }
            public object Value { get; }
            public int Releases => Volatile.Read(ref _releases);
            public int ReleaseThread { get; private set; }
            public Exception ReleaseError { get; set; }
            public void Release()
            {
                ReleaseThread = Thread.CurrentThread.ManagedThreadId;
                Interlocked.Increment(ref _releases);
                if (ReleaseError != null) throw ReleaseError;
            }
        }
    }
}
