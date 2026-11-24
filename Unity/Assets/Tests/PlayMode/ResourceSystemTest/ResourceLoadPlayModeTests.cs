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
    public class ResourceLoadPlayModeTests : ResourcePlayModeTestBase
    {
        [UnityTest]
        public IEnumerator InitializationIsOrderedSharedAndRetriable()
        {
            var loader = new GateLoader();
            var options = Options(loader);
            var firstAttempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0;
            var order = new List<string>();
            options.Initializers.Add(_ =>
            {
                order.Add("first");
                return ++attempts == 1 ? firstAttempt.Task : Task.CompletedTask;
            });
            options.Initializers.Add(_ => { order.Add("second"); return Task.CompletedTask; });
            var system = Track(new RS(options));

            var tooEarly = system.LoadAsync<string>(ResourceSource.Local, "before-initialize");
            yield return Finish(tooEarly);
            AssertFailure(tooEarly, DiagnosticCodes.LifecycleManagerDisposed);
            var first = system.InitializeAsync();
            Assert.That(system.InitializeAsync(), Is.SameAs(first));
            Assert.That(system.IsInitialized, Is.False);
            Assert.That(order, Is.EqualTo(new[] { "first" }));

            var failure = new InvalidOperationException("initialization failed");
            firstAttempt.SetException(failure);
            yield return Finish(first);
            Assert.That(Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult()), Is.SameAs(failure));
            Assert.That(system.IsInitialized, Is.False);

            var retry = system.InitializeAsync();
            yield return Await(retry);
            Assert.That(system.InitializeAsync(), Is.SameAs(retry));
            Assert.That(system.IsInitialized, Is.True);
            Assert.That(order, Is.EqualTo(new[] { "first", "first", "second" }));
            var request = system.LoadAsync<string>(ResourceSource.Local, "ready");
            yield return Until(() => loader.Count == 1);
            loader[0].Complete(new TestProduct("ready"));
            yield return Await(request);
            using (var reference = request.Result) Assert.That(reference.Value, Is.EqualTo("ready"));
        }

        [UnityTest]
        public IEnumerator SameKeySharesOneLoadAndCancellationOnlyEndsItsOwnWaiter()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var loader = new GateLoader();
            var system = Track(new RS(Options(loader)));
            yield return Await(system.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            {
                var canceled = system.LoadAsync<string>(ResourceSource.Local, "shared", token: cancellation.Token);
                var remaining = system.LoadAsync<string>(ResourceSource.Local, "shared");
                var alsoRemaining = system.LoadAsync<string>(ResourceSource.Local, "shared");
                yield return Until(() => loader.Count == 1);
                cancellation.Cancel();
                yield return Finish(canceled);
                Assert.That(canceled.IsCanceled, Is.True);
                Assert.That(loader[0].Token.IsCancellationRequested, Is.False);

                loader[0].Complete(new TestProduct("shared"));
                yield return Await(Task.WhenAll(remaining, alsoRemaining));
                using (var first = remaining.Result)
                using (var second = alsoRemaining.Result)
                {
                    Assert.That(first, Is.Not.SameAs(second));
                    Assert.That(first.Value, Is.EqualTo("shared"));
                    Assert.That(second.Value, Is.EqualTo(first.Value));
                    Assert.That(loader.Count, Is.EqualTo(1));
                    Assert.That(loader[0].EntryThread, Is.EqualTo(mainThread));
                }
            }
        }

        [UnityTest]
        public IEnumerator ThrowingProgressListenerDoesNotInterruptOtherWaiters()
        {
            var loader = new GateLoader();
            var system = Track(new RS(Options(loader)));
            yield return Await(system.InitializeAsync());
            var throwingValues = new List<double?>();
            var healthyValues = new List<double?>();
            // Synchronous IProgress makes the listener's exception visible to the reporting boundary.
            var throwing = system.LoadAsync<string>(ResourceSource.Local, "progress", new SyncProgress(value =>
            {
                throwingValues.Add(value.Fraction);
                throw new InvalidOperationException("progress listener failed");
            }));
            var healthy = system.LoadAsync<string>(ResourceSource.Local, "progress",
                new SyncProgress(value => healthyValues.Add(value.Fraction)));
            yield return Until(() => loader.Count == 1);
            yield return Await(Task.Run(() =>
            {
                loader[0].Report(0.25);
                loader[0].Report(0.75);
            }));
            loader[0].Complete(new TestProduct("complete"));
            yield return Await(Task.WhenAll(throwing, healthy));
            using (var first = throwing.Result)
            using (var second = healthy.Result)
            {
                Assert.That(throwingValues, Is.EqualTo(new double?[] { 0.25, 0.75 }));
                Assert.That(healthyValues, Is.EqualTo(throwingValues));
                Assert.That(first.Value, Is.EqualTo("complete"));
                Assert.That(second.Value, Is.EqualTo("complete"));
                Assert.That(loader.Count, Is.EqualTo(1));
            }
        }

        [UnityTest]
        public IEnumerator IdentitySharesEqualParametersButSeparatesParametersTypeAndSource()
        {
            var loader = new GateLoader();
            var options = Options(loader).AddLoader(ResourceSource.Remote, loader, new LoaderPolicy(4, null));
            var system = Track(new RS(options));
            yield return Await(system.InitializeAsync());
            var hd = new string(new[] { 'h', 'd' });
            var equivalentHd = new string(new[] { 'h', 'd' });
            Assert.That(equivalentHd, Is.Not.SameAs(hd));
            var plain = system.LoadAsync<string>(ResourceSource.Local, "identity");
            var parameterized = system.LoadAsync<string>(ResourceSource.Local, "identity", parameters: hd);
            var equivalent = system.LoadAsync<string>(ResourceSource.Local, "identity", parameters: equivalentHd);
            var different = system.LoadAsync<string>(ResourceSource.Local, "identity", parameters: "sd");
            var otherType = system.LoadAsync<byte[]>(ResourceSource.Local, "identity", parameters: hd);
            var otherSource = system.LoadAsync<string>(ResourceSource.Remote, "identity", parameters: hd);
            yield return Until(() => loader.Count >= 5);
            for (var index = 0; index < loader.Count; index++)
            {
                var pending = loader[index];
                var value = pending.Key.RequestedType == typeof(byte[])
                    ? (object)new byte[] { 7 }
                    : pending.Key.Source + ":" + (pending.Key.Parameters ?? "plain");
                pending.Complete(new TestProduct(value));
            }
            yield return Await(Task.WhenAll(plain, parameterized, equivalent, different, otherType, otherSource));
            using (var plainRef = plain.Result)
            using (var parameterRef = parameterized.Result)
            using (var equivalentRef = equivalent.Result)
            using (var differentRef = different.Result)
            using (var typedRef = otherType.Result)
            using (var remoteRef = otherSource.Result)
            {
                Assert.That(loader.Count, Is.EqualTo(5));
                Assert.That(parameterRef.Value, Is.SameAs(equivalentRef.Value));
                Assert.That(parameterRef.Key, Is.EqualTo(equivalentRef.Key));
                Assert.That(plainRef.Value, Is.EqualTo("Local:plain"));
                Assert.That(parameterRef.Value, Is.EqualTo("Local:hd"));
                Assert.That(differentRef.Value, Is.EqualTo("Local:sd"));
                Assert.That(typedRef.Value, Is.EqualTo(new byte[] { 7 }));
                Assert.That(remoteRef.Value, Is.EqualTo("Remote:hd"));
            }
        }

        [UnityTest]
        public IEnumerator BackendFailureEndsOneWaiterAndRemainingWaitersContinue()
        {
            var loader = new GateLoader();
            var system = Track(new RS(Options(loader)));
            yield return Await(system.InitializeAsync());
            var requests = Enumerable.Range(0, 3)
                .Select(_ => system.LoadAsync<string>(ResourceSource.Local, "shared-failure")).ToArray();
            yield return Until(() => loader.Count == 1);
            var cause = new InvalidOperationException("backend failed once");
            loader[0].Fail(cause);
            yield return Until(() => loader.Count == 2);
            loader[1].Complete(new TestProduct("recovered"));
            var all = Task.WhenAll(requests);
            yield return Finish(all);
            // Observe the aggregate only after completion; waiting synchronously would block Unity's context.
            Assert.That(all.Exception, Is.Not.Null);
            Assert.That(requests.Count(task => task.IsFaulted), Is.EqualTo(1));
            Assert.That(requests.Count(task => task.Status == TaskStatus.RanToCompletion), Is.EqualTo(2));
            foreach (var request in requests)
            {
                if (request.IsFaulted)
                    Assert.That(AssertFailure(request, DiagnosticCodes.AssetLoadFailed).ResourceError.Cause, Is.SameAs(cause));
                else
                    using (var reference = request.Result) Assert.That(reference.Value, Is.EqualTo("recovered"));
            }
            Assert.That(loader.Count, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator AllWaitersCancelLateProductIsReleasedAndNextRequestStartsFresh()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var loader = new GateLoader { ObserveCancellation = false };
            var system = Track(new RS(Options(loader)));
            yield return Await(system.InitializeAsync());
            using (var cancelFirst = new CancellationTokenSource())
            using (var cancelSecond = new CancellationTokenSource())
            {
                try
                {
                    var first = system.LoadAsync<string>(ResourceSource.Local, "abandoned", token: cancelFirst.Token);
                    var second = system.LoadAsync<string>(ResourceSource.Local, "abandoned", token: cancelSecond.Token);
                    yield return Until(() => loader.Count == 1);
                    cancelFirst.Cancel();
                    cancelSecond.Cancel();
                    yield return Finish(Task.WhenAll(first, second));
                    Assert.That(first.IsCanceled && second.IsCanceled, Is.True);
                    yield return Until(() => loader[0].Token.IsCancellationRequested);

                    // The old backend is still running. The next request must await its real exit.
                    loader.ObserveCancellation = true;
                    var retry = system.LoadAsync<string>(ResourceSource.Local, "abandoned");
                    var late = new TestProduct("late");
                    loader[0].Complete(late);
                    yield return Until(() => late.ReleaseCount == 1 && loader.Count == 2);
                    Assert.That(late.ReleaseThread, Is.EqualTo(mainThread));
                    Assert.That(system.Snapshot().Entries, Is.Empty);
                    loader[1].Complete(new TestProduct("fresh"));
                    yield return Await(retry);
                    using (var reference = retry.Result) Assert.That(reference.Value, Is.EqualTo("fresh"));
                    Assert.That(late.ReleaseCount, Is.EqualTo(1));
                }
                finally { loader.CancelPending(); }
            }
        }

        [UnityTest]
        public IEnumerator ReferencesAreIndependentAndUnloadOnlyReleasesSelectedUnusedProducts()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var loader = new GateLoader();
            var system = Track(new RS(Options(loader)));
            yield return Await(system.InitializeAsync());
            var request = system.LoadAsync<string>(ResourceSource.Local, "cached");
            yield return Until(() => loader.Count == 1);
            var product = new TestProduct("cached");
            loader[0].Complete(product);
            yield return Await(request);
            using (var first = request.Result)
            using (var retained = first.Retain())
            {
                var hit = system.LoadAsync<string>(ResourceSource.Local, "cached");
                yield return Await(hit);
                using (var cached = hit.Result)
                {
                    first.Dispose();
                    first.Dispose();
                    Assert.That(first.IsValid, Is.False);
                    Assert.That(Assert.Throws<ResourceException>(() => { var ignored = first.Value; }).ResourceError.DiagnosticCode,
                        Is.EqualTo(DiagnosticCodes.AssetInvalidated));
                    Assert.That(retained.Value, Is.EqualTo("cached"));
                    Assert.That(cached.Value, Is.EqualTo(retained.Value));
                    Assert.That(loader.Count, Is.EqualTo(1));
                    var skipped = system.UnloadAsync();
                    yield return Await(skipped);
                    Assert.That(skipped.Result.SkippedInUse.Count, Is.EqualTo(1));
                    Assert.That(skipped.Result.Released, Is.Empty);
                    Assert.That(product.ReleaseCount, Is.Zero);
                }
            }

            var otherRequest = system.LoadAsync<string>(ResourceSource.Local, "other");
            yield return Until(() => loader.Count == 2);
            var other = new TestProduct("other");
            loader[1].Complete(other);
            yield return Await(otherRequest);
            otherRequest.Result.Dispose();
            var selected = system.UnloadAsync(key => key.Address == "cached");
            yield return Await(selected);
            Assert.That(selected.Result.Released.Select(key => key.Address), Is.EqualTo(new[] { "cached" }));
            Assert.That(product.ReleaseCount, Is.EqualTo(1));
            Assert.That(product.ReleaseThread, Is.EqualTo(mainThread));
            Assert.That(other.ReleaseCount, Is.Zero);
            yield return Await(system.UnloadAsync());
            yield return Await(system.UnloadAsync());
            Assert.That(other.ReleaseCount, Is.EqualTo(1));
            Assert.That(product.ReleaseCount, Is.EqualTo(1));
            Assert.That(system.Snapshot().Entries, Is.Empty);
        }

        [UnityTest]
        public IEnumerator FailedUnloadKeepsResidueWhileOtherProductsReleaseAndRetryRunsOnMainThread()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var loader = new GateLoader();
            var system = Track(new RS(Options(loader)));
            yield return Await(system.InitializeAsync());
            var failure = new InvalidOperationException("release failed once");
            var broken = new TestProduct("broken") { ReleaseError = failure };
            var healthy = new TestProduct("healthy");
            var brokenRequest = system.LoadAsync<string>(ResourceSource.Local, "broken");
            var healthyRequest = system.LoadAsync<string>(ResourceSource.Local, "healthy");
            yield return Until(() => loader.Count == 2);
            for (var index = 0; index < 2; index++)
                loader[index].Complete(loader[index].Key.Address == "broken" ? broken : healthy);
            yield return Await(Task.WhenAll(brokenRequest, healthyRequest));
            brokenRequest.Result.Dispose();
            healthyRequest.Result.Dispose();

            var unload = system.UnloadAsync();
            yield return Await(unload);
            Assert.That(unload.Result.Failed.Count, Is.EqualTo(1));
            Assert.That(unload.Result.Failed[0].Error, Is.SameAs(failure));
            Assert.That(unload.Result.Failed[0].Key.Address, Is.EqualTo("broken"));
            Assert.That(unload.Result.Released.Select(key => key.Address), Is.EqualTo(new[] { "healthy" }));
            Assert.That(broken.ReleaseCount, Is.EqualTo(1));
            Assert.That(healthy.ReleaseCount, Is.EqualTo(1));
            Assert.That(broken.ReleaseThread, Is.EqualTo(mainThread));
            Assert.That(healthy.ReleaseThread, Is.EqualTo(mainThread));
            Assert.That(system.Snapshot().Entries, Is.Empty);
            Assert.That(system.Snapshot().Residues.Count, Is.EqualTo(1));

            // A failed old product must not remain reusable or block a fresh product with the same key.
            var reload = system.LoadAsync<string>(ResourceSource.Local, "broken");
            yield return Until(() => loader.Count == 3);
            var replacement = new TestProduct("replacement");
            loader[2].Complete(replacement);
            yield return Await(reload);
            using (var reference = reload.Result) Assert.That(reference.Value, Is.EqualTo("replacement"));

            // ResourceCache explicitly retries residues on the next business-requested unload.
            broken.ReleaseError = null;
            var retry = system.UnloadAsync(key => key.Address == "broken");
            yield return Await(retry);
            Assert.That(retry.Result.Failed, Is.Empty);
            Assert.That(retry.Result.Released.Count, Is.EqualTo(2));
            Assert.That(broken.ReleaseCount, Is.EqualTo(2));
            Assert.That(broken.ReleaseThread, Is.EqualTo(mainThread));
            Assert.That(replacement.ReleaseCount, Is.EqualTo(1));
            Assert.That(replacement.ReleaseThread, Is.EqualTo(mainThread));
            Assert.That(system.Snapshot().Residues, Is.Empty);
            yield return Await(system.UnloadAsync());
            Assert.That(broken.ReleaseCount, Is.EqualTo(2));
            Assert.That(healthy.ReleaseCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ConversionAndAllProductReleasesRunOnUnityMainThread()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var loader = new GateLoader();
            var output = new TestProduct("converted");
            var converter = new TestConverter(_ => output);
            var system = Track(new RS(Options(loader).AddConverter<string>(converter)));
            yield return Await(system.InitializeAsync());
            var request = system.LoadAsync<string>(ResourceSource.Local, "convert");
            yield return Until(() => loader.Count == 1);
            var source = new TestProduct(new byte[] { 1, 2 });
            loader[0].Complete(source);
            yield return Await(request);
            using (var reference = request.Result)
            {
                Assert.That(reference.Value, Is.EqualTo("converted"));
                Assert.That(converter.BeforeAwaitThread, Is.EqualTo(mainThread));
                Assert.That(converter.AfterAwaitThread, Is.EqualTo(mainThread));
                Assert.That(source.ReleaseCount, Is.EqualTo(1));
                Assert.That(source.ReleaseThread, Is.EqualTo(mainThread));
                Assert.That(output.ReleaseCount, Is.Zero);
            }
            yield return Await(system.UnloadAsync());
            Assert.That(output.ReleaseCount, Is.EqualTo(1));
            Assert.That(output.ReleaseThread, Is.EqualTo(mainThread));
        }

        [UnityTest]
        public IEnumerator MissingFailedAndWrongTypeConversionsReleaseTheirProducts()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            for (var scenario = 0; scenario < 3; scenario++)
            {
                var loader = new GateLoader();
                var options = Options(loader);
                var cause = new FormatException("invalid bytes");
                var wrongOutput = new TestProduct(123);
                if (scenario == 1) options.AddConverter<string>(new TestConverter(_ => throw cause));
                if (scenario == 2) options.AddConverter<string>(new TestConverter(_ => wrongOutput));
                var system = Track(new RS(options));
                yield return Await(system.InitializeAsync());
                var request = system.LoadAsync<string>(ResourceSource.Local, "invalid-conversion");
                yield return Until(() => loader.Count == 1);
                var source = new TestProduct(new byte[] { 255 });
                loader[0].Complete(source);
                yield return Finish(request);
                var failure = AssertFailure(request, scenario == 1
                    ? DiagnosticCodes.AssetConversionFailed : DiagnosticCodes.AssetTypeMismatch);
                if (scenario == 1) Assert.That(failure.ResourceError.Cause, Is.SameAs(cause));
                Assert.That(source.ReleaseCount, Is.EqualTo(1), "scenario " + scenario);
                Assert.That(source.ReleaseThread, Is.EqualTo(mainThread));
                if (scenario == 2)
                {
                    Assert.That(wrongOutput.ReleaseCount, Is.EqualTo(1));
                    Assert.That(wrongOutput.ReleaseThread, Is.EqualTo(mainThread));
                }
                Assert.That(system.Snapshot().Entries, Is.Empty);
                Assert.That(system.Snapshot().Residues, Is.Empty);
            }
        }

        [UnityTest]
        public IEnumerator ConcurrencyCountsDistinctBackendLoadsNotSharedWaiters()
        {
            var loader = new GateLoader();
            var system = Track(new RS(Options(loader, new LoaderPolicy(2, null))));
            yield return Await(system.InitializeAsync());
            var requests = new[] { "a", "a", "b", "c", "d" }
                .Select(key => system.LoadAsync<string>(ResourceSource.Local, key)).ToArray();
            yield return Until(() => loader.Count == 2);
            Assert.That(loader.PeakActive, Is.EqualTo(2));
            loader[0].Complete(new TestProduct(loader[0].Key.Address));
            yield return Until(() => loader.Count == 3);
            loader[1].Complete(new TestProduct(loader[1].Key.Address));
            yield return Until(() => loader.Count == 4);
            loader[2].Complete(new TestProduct(loader[2].Key.Address));
            loader[3].Complete(new TestProduct(loader[3].Key.Address));
            yield return Await(Task.WhenAll(requests));
            foreach (var request in requests)
                using (var reference = request.Result) Assert.That(reference.Value, Is.EqualTo(reference.Key.Address));
            Assert.That(loader.Count, Is.EqualTo(4));
            Assert.That(loader.PeakActive, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator TimeoutIncludesQueueWaitAndLateCompletionIsDiscarded()
        {
            var loader = new GateLoader { ObserveCancellation = false };
            var system = Track(new RS(Options(loader, new LoaderPolicy(1, TimeSpan.FromMilliseconds(400)))));
            yield return Await(system.InitializeAsync());
            try
            {
                var active = system.LoadAsync<string>(ResourceSource.Local, "active");
                yield return Until(() => loader.Count == 1);
                var queued = system.LoadAsync<string>(ResourceSource.Local, "queued");
                yield return Finish(active);
                yield return Finish(queued);
                AssertFailure(active, DiagnosticCodes.RequestTimeout);
                AssertFailure(queued, DiagnosticCodes.RequestTimeout);
                Assert.That(loader[0].Token.IsCancellationRequested, Is.True);
                Assert.That(loader.Count, Is.EqualTo(1), "排队请求应在到达后端之前消耗完总超时。");

                var late = new TestProduct("too late");
                loader[0].Complete(late);
                yield return Until(() => late.ReleaseCount == 1);
                Assert.That(system.Snapshot().Entries, Is.Empty);
            }
            finally { loader.CancelPending(); }
        }

        [UnityTest]
        public IEnumerator ExternalRegistrationUsesPolicySnapshotAndExplicitOverrideIsNotSticky()
        {
            var options = Options().AddExternalLoader(ResourceSource.AssetBundle,
                new LoaderPolicy(1, TimeSpan.FromMilliseconds(400)));
            var system = Track(new RS(options));
            // The runtime must use its own snapshot even if the assembly object changes afterwards.
            options.AddExternalLoader(ResourceSource.AssetBundle, new LoaderPolicy(4, null));
            yield return Await(system.InitializeAsync());
            var missing = system.LoadAsync<string>(ResourceSource.AssetBundle, "missing");
            yield return Finish(missing);
            AssertFailure(missing, DiagnosticCodes.LoaderNotRegistered);

            var configured = new GateLoader();
            system.RegisterLoader(ResourceSource.AssetBundle, configured);
            var timed = system.LoadAsync<string>(ResourceSource.AssetBundle, "configured-timeout");
            yield return Until(() => configured.Count == 1);
            yield return Finish(timed);
            AssertFailure(timed, DiagnosticCodes.RequestTimeout);

            var overridden = new GateLoader();
            system.RegisterLoader(ResourceSource.AssetBundle, overridden, new LoaderPolicy(2, null));
            var first = system.LoadAsync<string>(ResourceSource.AssetBundle, "override-a");
            var second = system.LoadAsync<string>(ResourceSource.AssetBundle, "override-b");
            yield return Until(() => overridden.Count == 2);
            Assert.That(overridden.PeakActive, Is.EqualTo(2));
            overridden[0].Complete(new TestProduct("override"));
            overridden[1].Complete(new TestProduct("override"));
            yield return Await(Task.WhenAll(first, second));
            first.Result.Dispose();
            second.Result.Dispose();

            var restored = new GateLoader();
            system.RegisterLoader(ResourceSource.AssetBundle, restored);
            var restoredTimeout = system.LoadAsync<string>(ResourceSource.AssetBundle, "restored-timeout");
            yield return Until(() => restored.Count == 1);
            yield return Finish(restoredTimeout);
            AssertFailure(restoredTimeout, DiagnosticCodes.RequestTimeout);
        }

        [UnityTest]
        public IEnumerator ReplacementPreservesInFlightAndCachedProductsAndDisposeStopsPendingLoads()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var system = Track(new RS(Options()));
            yield return Await(system.InitializeAsync());
            var old = new GateLoader();
            system.RegisterLoader(ResourceSource.AssetBundle, old, new LoaderPolicy(1, null));
            var inFlight = system.LoadAsync<string>(ResourceSource.AssetBundle, "old");
            yield return Until(() => old.Count == 1);

            var fresh = new GateLoader();
            system.RegisterLoader(ResourceSource.AssetBundle, fresh);
            var newRequest = system.LoadAsync<string>(ResourceSource.AssetBundle, "new");
            yield return Until(() => fresh.Count == 1);
            var newProduct = new TestProduct("new");
            fresh[0].Complete(newProduct);
            yield return Await(newRequest);
            Assert.That(old[0].Token.IsCancellationRequested, Is.False);
            var oldProduct = new TestProduct("old");
            old[0].Complete(oldProduct);
            yield return Await(inFlight);
            using (var oldReference = inFlight.Result)
            using (var newReference = newRequest.Result)
            {
                Assert.That(oldReference.Value, Is.EqualTo("old"));
                Assert.That(newReference.Value, Is.EqualTo("new"));
                var cached = system.LoadAsync<string>(ResourceSource.AssetBundle, "old");
                yield return Await(cached);
                using (var reference = cached.Result) Assert.That(reference.Value, Is.EqualTo("old"));
                Assert.That(fresh.Count, Is.EqualTo(1));
            }

            var pending = system.LoadAsync<string>(ResourceSource.AssetBundle, "stop-on-dispose");
            yield return Until(() => fresh.Count == 2);
            yield return Await(system.DisposeAsync().AsTask());
            yield return Finish(pending);
            Assert.That(pending.IsCanceled, Is.True);
            Assert.That(fresh[1].Token.IsCancellationRequested, Is.True);
            Assert.That(oldProduct.ReleaseCount, Is.EqualTo(1));
            Assert.That(newProduct.ReleaseCount, Is.EqualTo(1));
            Assert.That(oldProduct.ReleaseThread, Is.EqualTo(mainThread));
            Assert.That(newProduct.ReleaseThread, Is.EqualTo(mainThread));
            var disposed = system.LoadAsync<string>(ResourceSource.AssetBundle, "after-dispose");
            yield return Finish(disposed);
            AssertFailure(disposed, DiagnosticCodes.LifecycleSystemDisposed);
        }

        private static ResourceSystemOptions Options(ILoader loader = null, LoaderPolicy? policy = null)
        {
            var options = new ResourceSystemOptions { ExecutionContext = new UnityExecutionContext() };
            if (loader != null) options.AddLoader(ResourceSource.Local, loader, policy ?? new LoaderPolicy(4, null));
            return options;
        }

        private static ResourceException AssertFailure(Task task, string diagnostic)
        {
            Assert.That(task.IsCompleted, Is.True);
            var error = Assert.Throws<ResourceException>(() => task.GetAwaiter().GetResult());
            Assert.That(error.ResourceError.DiagnosticCode, Is.EqualTo(diagnostic));
            return error;
        }

        /// <summary> 可控后端：实际加载由核心在 Unity 执行上下文内启动。</summary>
        private sealed class GateLoader : ILoader
        {
            private readonly object _gate = new object();
            private readonly List<PendingLoad> _pending = new List<PendingLoad>();
            private int _active;
            private int _peakActive;
            public volatile bool ObserveCancellation = true;
            public int Count { get { lock (_gate) return _pending.Count; } }
            public int PeakActive { get { lock (_gate) return _peakActive; } }
            public PendingLoad this[int index] { get { lock (_gate) return _pending[index]; } }

            public async Task<IResource> LoadAsync(ResourceKey key,
                IProgress<OperationProgress> progress, CancellationToken cancellation)
            {
                var observeCancellation = ObserveCancellation;
                var pending = new PendingLoad(key, cancellation, progress);
                lock (_gate)
                {
                    _pending.Add(pending);
                    _active++;
                    _peakActive = Math.Max(_peakActive, _active);
                }
                using (observeCancellation ? cancellation.Register(pending.Cancel) : default(CancellationTokenRegistration))
                {
                    try
                    {
                        return await pending.Completion.Task.ConfigureAwait(false);
                    }
                    finally { lock (_gate) _active--; }
                }
            }

            public void CancelPending()
            {
                PendingLoad[] pending;
                lock (_gate) pending = _pending.ToArray();
                foreach (var load in pending) load.Cancel();
            }
        }

        private sealed class PendingLoad
        {
            public readonly ResourceKey Key;
            public readonly CancellationToken Token;
            public readonly int EntryThread = Thread.CurrentThread.ManagedThreadId;
            public readonly TaskCompletionSource<IResource> Completion =
                new TaskCompletionSource<IResource>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly IProgress<OperationProgress> _progress;

            public PendingLoad(ResourceKey key, CancellationToken token, IProgress<OperationProgress> progress)
            {
                Key = key;
                Token = token;
                _progress = progress;
            }
            public void Complete(IResource product) => Completion.TrySetResult(product);
            public void Fail(Exception error) => Completion.TrySetException(error);
            public void Cancel() => Completion.TrySetCanceled();
            public void Report(double fraction) => _progress?.Report(new OperationProgress(fraction));
        }

        private sealed class TestProduct : IResource
        {
            private int _releaseCount;
            public TestProduct(object value) { Value = value; }
            public object Value { get; }
            public int ReleaseCount => Volatile.Read(ref _releaseCount);
            public int ReleaseThread { get; private set; }
            public Exception ReleaseError { get; set; }
            public void Release()
            {
                ReleaseThread = Thread.CurrentThread.ManagedThreadId;
                Interlocked.Increment(ref _releaseCount);
                if (ReleaseError != null) throw ReleaseError;
            }
        }

        private sealed class SyncProgress : IProgress<OperationProgress>
        {
            private readonly Action<OperationProgress> _report;
            public SyncProgress(Action<OperationProgress> report) { _report = report; }
            public void Report(OperationProgress value) => _report(value);
        }

        private sealed class TestConverter : IProductConverter
        {
            private readonly Func<IResource, IResource> _convert;
            public int BeforeAwaitThread { get; private set; }
            public int AfterAwaitThread { get; private set; }
            public TestConverter(Func<IResource, IResource> convert) { _convert = convert; }

            public async Task<IResource> ConvertAsync(IResource source, ResourceKey key, CancellationToken cancellation)
            {
                BeforeAwaitThread = Thread.CurrentThread.ManagedThreadId;
                await Task.Yield();
                AfterAwaitThread = Thread.CurrentThread.ManagedThreadId;
                cancellation.ThrowIfCancellationRequested();
                return _convert(source);
            }
        }
    }
}
