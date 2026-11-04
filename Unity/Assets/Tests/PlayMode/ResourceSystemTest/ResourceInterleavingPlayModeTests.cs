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
    /// <summary>用后端门闩和进入/退出顺序固定取消交错，不用延时猜测调度时机。</summary>
    public class ResourceInterleavingPlayModeTests : ResourcePlayModeTestBase
    {
        private readonly List<ControlledLoader> _loaders = new List<ControlledLoader>();
        private readonly List<Task<ResourceRef<string>>> _requests = new List<Task<ResourceRef<string>>>();
        private readonly List<Task> _sharedShutdowns = new List<Task>();

        [UnityTest]
        public IEnumerator CancelCooperativeAThenImmediatelyLoadBReusesTheSerialSlot()
        {
            var loader = CreateLoader(observeCancellation: true);
            var system = CreateSystem(loader, 1);
            yield return Await(system.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    var a = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "A", token: cancellation.Token));
                    yield return Until(() => loader.Count >= 1);
                    cancellation.Cancel();
                    var b = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "B"));
                    yield return Finish(a);
                    Assert.That(a.IsCanceled, Is.True);
                    yield return Until(() => loader.Count >= 2);
                    Assert.That(loader[0].Token.IsCancellationRequested, Is.True);
                    Assert.That(loader[1].Key.Address, Is.EqualTo("B"));
                    Assert.That(loader[1].EnterOrder, Is.GreaterThan(loader[0].ExitOrder));
                    Assert.That(loader[0].ExitOrder, Is.GreaterThan(0));
                    loader[1].Complete(new TrackedProduct("B"));
                    yield return Await(b);
                    using (var reference = b.Result) Assert.That(reference.Value, Is.EqualTo("B"));
                    Assert.That(loader.PeakActive, Is.EqualTo(1));
                }
                finally { loader.CancelPending(); }
            }
        }

        [UnityTest]
        public IEnumerator CancelNonCooperativeAThenBWaitsForItsRealExitInSerialMode()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var loader = CreateLoader(observeCancellation: false);
            var system = CreateSystem(loader, 1);
            yield return Await(system.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    var a = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "A", token: cancellation.Token));
                    yield return Until(() => loader.Count >= 1);
                    cancellation.Cancel();
                    var b = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "B"));
                    yield return Finish(a);
                    Assert.That(a.IsCanceled, Is.True);
                    yield return Until(() => loader[0].Token.IsCancellationRequested);
                    Assert.That(loader[0].HasExited, Is.False);

                    // Canceling a waiter cannot release a slot still occupied by real backend work.
                    var late = new TrackedProduct("late A");
                    loader[0].Complete(late);
                    yield return Until(() => loader.Count >= 2);
                    Assert.That(loader[1].Key.Address, Is.EqualTo("B"));
                    Assert.That(loader[1].EnterOrder, Is.GreaterThan(loader[0].ExitOrder));
                    Assert.That(late.ReleaseCount, Is.EqualTo(1));
                    Assert.That(late.ReleaseThread, Is.EqualTo(mainThread));
                    loader[1].Complete(new TrackedProduct("B"));
                    yield return Await(b);
                    using (var reference = b.Result) Assert.That(reference.Value, Is.EqualTo("B"));
                    Assert.That(loader.PeakActive, Is.EqualTo(1));
                    Assert.That(system.Snapshot().Entries.Any(entry => entry.Key.Address == "A"), Is.False);
                }
                finally { loader.CancelPending(); }
            }
        }

        [UnityTest]
        public IEnumerator CancelNonCooperativeAAllowsBToUseAnotherParallelSlot()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var loader = CreateLoader(observeCancellation: false);
            var system = CreateSystem(loader, 2);
            yield return Await(system.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    var a = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "A", token: cancellation.Token));
                    yield return Until(() => loader.Count >= 1);
                    cancellation.Cancel();
                    var b = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "B"));
                    yield return Finish(a);
                    Assert.That(a.IsCanceled, Is.True);
                    yield return Until(() => loader.Count >= 2);
                    Assert.That(loader[0].HasExited, Is.False);
                    Assert.That(loader[1].Key.Address, Is.EqualTo("B"));
                    Assert.That(loader[1].Token.IsCancellationRequested, Is.False);
                    loader[1].Complete(new TrackedProduct("B before A exits"));
                    yield return Await(b);
                    using (var reference = b.Result) Assert.That(reference.Value, Is.EqualTo("B before A exits"));
                    Assert.That(loader[0].HasExited, Is.False);

                    var late = new TrackedProduct("late A");
                    loader[0].Complete(late);
                    yield return Until(() => late.ReleaseCount == 1);
                    Assert.That(late.ReleaseThread, Is.EqualTo(mainThread));
                    Assert.That(loader.PeakActive, Is.EqualTo(2));
                    Assert.That(loader[1].EnterOrder, Is.LessThan(loader[0].ExitOrder));
                }
                finally { loader.CancelPending(); }
            }
        }

        [UnityTest]
        public IEnumerator CancelQueuedABeforeTheSlotOpensSkipsItAndLetsBRun()
        {
            var loader = CreateLoader(observeCancellation: true);
            var system = CreateSystem(loader, 1);
            yield return Await(system.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    var blocker = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "blocker"));
                    yield return Until(() => loader.Count >= 1);
                    var a = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "A", token: cancellation.Token));
                    cancellation.Cancel();
                    var b = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "B"));
                    yield return Finish(a);
                    Assert.That(a.IsCanceled, Is.True);
                    Assert.That(loader.Count, Is.EqualTo(1));

                    loader[0].Complete(new TrackedProduct("blocker"));
                    yield return Await(blocker);
                    using (var reference = blocker.Result) Assert.That(reference.Value, Is.EqualTo("blocker"));
                    yield return Until(() => loader.Count >= 2);
                    Assert.That(loader[1].Key.Address, Is.EqualTo("B"), "已取消的排队 A 不得进入后端。");
                    loader[1].Complete(new TrackedProduct("B"));
                    yield return Await(b);
                    using (var reference = b.Result) Assert.That(reference.Value, Is.EqualTo("B"));
                    Assert.That(loader.Addresses, Is.EqualTo(new[] { "blocker", "B" }));
                    Assert.That(loader.PeakActive, Is.EqualTo(1));
                }
                finally { loader.CancelPending(); }
            }
        }

        [UnityTest]
        public IEnumerator CancelOneSharedAWaiterKeepsItsOtherWaiterAndThenUnblocksB()
        {
            var loader = CreateLoader(observeCancellation: true);
            var system = CreateSystem(loader, 1);
            yield return Await(system.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    var canceledA = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "A", token: cancellation.Token));
                    var remainingA = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "A"));
                    yield return Until(() => loader.Count >= 1);
                    cancellation.Cancel();
                    var b = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "B"));
                    yield return Finish(canceledA);
                    Assert.That(canceledA.IsCanceled, Is.True);
                    Assert.That(loader[0].Token.IsCancellationRequested, Is.False);
                    loader[0].Complete(new TrackedProduct("shared A"));
                    yield return Await(remainingA);
                    using (var reference = remainingA.Result) Assert.That(reference.Value, Is.EqualTo("shared A"));
                    yield return Until(() => loader.Count >= 2);
                    Assert.That(loader[1].Key.Address, Is.EqualTo("B"));
                    Assert.That(loader[1].EnterOrder, Is.GreaterThan(loader[0].ExitOrder));
                    loader[1].Complete(new TrackedProduct("B"));
                    yield return Await(b);
                    using (var reference = b.Result) Assert.That(reference.Value, Is.EqualTo("B"));
                    Assert.That(loader.Addresses, Is.EqualTo(new[] { "A", "B" }));
                    Assert.That(loader.PeakActive, Is.EqualTo(1));
                }
                finally { loader.CancelPending(); }
            }
        }

        [UnityTest]
        public IEnumerator SameKeyRequestAfterCancellationWaitsForOldRoundAndNeverReceivesItsProduct()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var loader = CreateLoader(observeCancellation: false);
            var system = CreateSystem(loader, 2);
            yield return Await(system.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    var oldA = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "A", token: cancellation.Token));
                    yield return Until(() => loader.Count >= 1);
                    cancellation.Cancel();
                    // Observe completed cancellation before asking for a new round of the same identity.
                    yield return Finish(oldA);
                    Assert.That(oldA.IsCanceled, Is.True);
                    var newA = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "A"));
                    var b = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "B"));
                    yield return Until(() => loader.Count >= 2);
                    Assert.That(loader[1].Key.Address, Is.EqualTo("B"), "新 A 要等待旧轮退出，空闲名额应供独立的 B 使用。");
                    Assert.That(newA.IsCompleted, Is.False);
                    Assert.That(loader[0].HasExited, Is.False);

                    var late = new TrackedProduct("old A must not escape");
                    loader[0].Complete(late);
                    yield return Until(() => loader.Count >= 3);
                    Assert.That(loader[2].Key.Address, Is.EqualTo("A"));
                    Assert.That(loader[2].EnterOrder, Is.GreaterThan(loader[0].ExitOrder));
                    Assert.That(late.ReleaseCount, Is.EqualTo(1));
                    Assert.That(late.ReleaseThread, Is.EqualTo(mainThread));
                    loader[2].Complete(new TrackedProduct("new A"));
                    loader[1].Complete(new TrackedProduct("B"));
                    yield return Await(Task.WhenAll(newA, b));
                    using (var newReference = newA.Result)
                    using (var bReference = b.Result)
                    {
                        Assert.That(newReference.Value, Is.EqualTo("new A"));
                        Assert.That(bReference.Value, Is.EqualTo("B"));
                    }
                    Assert.That(loader.Addresses, Is.EqualTo(new[] { "A", "B", "A" }));
                    Assert.That(loader.PeakActive, Is.EqualTo(2));
                }
                finally { loader.CancelPending(); }
            }
        }

        [UnityTest]
        public IEnumerator LateFailureFromCanceledADoesNotFailOrCancelB()
        {
            var loader = CreateLoader(observeCancellation: false);
            var system = CreateSystem(loader, 2);
            yield return Await(system.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    var a = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "A", token: cancellation.Token));
                    yield return Until(() => loader.Count >= 1);
                    cancellation.Cancel();
                    var b = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "B"));
                    yield return Finish(a);
                    Assert.That(a.IsCanceled, Is.True);
                    yield return Until(() => loader.Count >= 2);
                    loader[0].Fail(new InvalidOperationException("late failure from canceled A"));
                    yield return Until(() => loader[0].HasExited);
                    Assert.That(loader[1].Token.IsCancellationRequested, Is.False);
                    loader[1].Complete(new TrackedProduct("B survived"));
                    yield return Await(b);
                    using (var reference = b.Result) Assert.That(reference.Value, Is.EqualTo("B survived"));
                    Assert.That(a.IsCanceled, Is.True);
                    Assert.That(system.Snapshot().Entries.Any(entry => entry.Key.Address == "A"), Is.False);
                    Assert.That(system.Snapshot().Residues, Is.Empty);
                }
                finally { loader.CancelPending(); }
            }
        }

        [UnityTest]
        public IEnumerator CanceledLocalAHasNoEffectOnSameAddressRemoteBOrItsConcurrencySlot()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var loader = CreateLoader(observeCancellation: false);
            var options = Options(loader, 1).AddLoader(ResourceSource.Remote, loader, new LoaderPolicy(1, null));
            var system = Track(new RS(options));
            yield return Await(system.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    var local = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "same-address", token: cancellation.Token));
                    yield return Until(() => loader.Count >= 1);
                    cancellation.Cancel();
                    var remote = TrackRequest(system.LoadAsync<string>(ResourceSource.Remote, "same-address"));
                    yield return Finish(local);
                    Assert.That(local.IsCanceled, Is.True);
                    yield return Until(() => loader.Count >= 2);
                    Assert.That(loader[0].Token.IsCancellationRequested, Is.True);
                    Assert.That(loader[0].HasExited, Is.False);
                    Assert.That(loader[1].Key.Source, Is.EqualTo(ResourceSource.Remote));
                    Assert.That(loader[1].Token.IsCancellationRequested, Is.False);
                    loader[1].Complete(new TrackedProduct("remote B"));
                    yield return Await(remote);
                    using (var reference = remote.Result) Assert.That(reference.Value, Is.EqualTo("remote B"));
                    var lateLocal = new TrackedProduct("late local A");
                    loader[0].Complete(lateLocal);
                    yield return Until(() => lateLocal.ReleaseCount == 1);
                    Assert.That(lateLocal.ReleaseThread, Is.EqualTo(mainThread));
                    Assert.That(loader.PeakActive, Is.EqualTo(2), "每来源各自配置一个名额，不应变成全局单名额。");
                    Assert.That(system.Snapshot().Entries.Select(entry => entry.Key.Source),
                        Is.EqualTo(new[] { ResourceSource.Remote }));
                }
                finally { loader.CancelPending(); }
            }
        }

        [UnityTest]
        public IEnumerator ThrowingLateProgressReplayDoesNotLeaveAGhostWaiterAfterBothRequestsCancel()
        {
            var loader = CreateLoader(observeCancellation: false);
            var system = CreateSystem(loader, 1);
            yield return Await(system.InitializeAsync());
            using (var cancelFirst = new CancellationTokenSource())
            using (var cancelLate = new CancellationTokenSource())
            {
                try
                {
                    var first = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "replayed-progress", token: cancelFirst.Token));
                    yield return Until(() => loader.Count >= 1);
                    loader[0].Report(0.5);
                    var replayCalls = 0;
                    var late = TrackRequest(system.LoadAsync<string>(ResourceSource.Local, "replayed-progress",
                        new SyncProgress(_ =>
                        {
                            replayCalls++;
                            throw new InvalidOperationException("late progress replay failed");
                        }), cancelLate.Token));

                    // Both callers leave even if broken replay handling already faulted the late request.
                    cancelFirst.Cancel();
                    cancelLate.Cancel();
                    yield return Finish(first);
                    yield return Finish(late);
                    var replayFailure = late.Exception?.GetBaseException();
                    Assert.That(replayCalls, Is.EqualTo(1), "迟到监听器必须收到本轮已有的进度回放。");
                    Assert.That(first.IsCanceled && late.IsCanceled && loader[0].Token.IsCancellationRequested,
                        Is.True, "进度异常必须隔离；两位请求取消后底层也必须收到取消，不能遗留等待者。"
                            + " first=" + first.Status + ", late=" + late.Status
                            + ", roundCanceled=" + loader[0].Token.IsCancellationRequested
                            + ", replayFailure=" + replayFailure);
                }
                finally
                {
                    cancelFirst.Cancel();
                    cancelLate.Cancel();
                    loader.CancelPending();
                }
            }
        }

        [UnityTest]
        public IEnumerator ThrowingReplayAndThrowingDiagnosticStillAllowTheLastWaiterToCancelExecution()
        {
            var backend = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replayFailure = new InvalidOperationException("replay failed");
            var roundToken = CancellationToken.None;
            var diagnoses = 0;
            Exception diagnosed = null;
            var shared = new SharedOperation<string, int>(async (_, progress, token) =>
            {
                roundToken = token;
                progress.Report(new OperationProgress(0.5));
                using (token.Register(() => backend.TrySetCanceled(token)))
                    return await backend.Task.ConfigureAwait(false);
            }, NoWaitersPolicy.CancelExecution, diagnose: error =>
            {
                diagnosed = error;
                Interlocked.Increment(ref diagnoses);
                throw new InvalidOperationException("diagnostic observer failed");
            });
            using (var firstCancellation = new CancellationTokenSource())
            using (var lateCancellation = new CancellationTokenSource())
            {
                Task<int> first = null;
                Task<int> late = null;
                try
                {
                    first = shared.GetAsync("A", token: firstCancellation.Token);
                    late = shared.GetAsync("A", new SyncProgress(_ => throw replayFailure), lateCancellation.Token);
                    Assert.That(diagnoses, Is.EqualTo(1));
                    Assert.That(diagnosed, Is.SameAs(replayFailure));
                    firstCancellation.Cancel();
                    lateCancellation.Cancel();
                    yield return Finish(first);
                    yield return Finish(late);
                    Assert.That(first.IsCanceled, Is.True);
                    Assert.That(late.IsCanceled, Is.True);
                    Assert.That(roundToken.IsCancellationRequested, Is.True,
                        "观察者和诊断回调均失败，也不能把已登记的等待者留在执行计数中。");
                }
                finally
                {
                    firstCancellation.Cancel();
                    lateCancellation.Cancel();
                    _sharedShutdowns.Add(shared.StopAsync());
                    if (first != null && first.IsFaulted) _ = first.Exception;
                    if (late != null && late.IsFaulted) _ = late.Exception;
                }
            }
        }

        [UnityTest]
        public IEnumerator ReplayCanCancelItselfAndReenterToJoinAnotherWaiterWithoutLosingTheCount()
        {
            var backend = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var roundToken = CancellationToken.None;
            var backendCancellations = 0;
            var shared = new SharedOperation<string, int>(async (_, progress, token) =>
            {
                roundToken = token;
                progress.Report(new OperationProgress(0.5));
                using (token.Register(() =>
                {
                    Interlocked.Increment(ref backendCancellations);
                    backend.TrySetCanceled(token);
                })) return await backend.Task.ConfigureAwait(false);
            }, NoWaitersPolicy.CancelExecution);
            using (var firstCancellation = new CancellationTokenSource())
            using (var replayCancellation = new CancellationTokenSource())
            using (var joinedCancellation = new CancellationTokenSource())
            {
                Task<int> first = null;
                Task<int> replay = null;
                Task<int> joined = null;
                var replayCalls = 0;
                try
                {
                    first = shared.GetAsync("A", token: firstCancellation.Token);
                    replay = shared.GetAsync("A", new SyncProgress(_ =>
                    {
                        replayCalls++;
                        replayCancellation.Cancel();
                        joined = shared.GetAsync("A", token: joinedCancellation.Token);
                    }), replayCancellation.Token);
                    yield return Finish(replay);
                    Assert.That(replayCalls, Is.EqualTo(1));
                    Assert.That(replay.IsCanceled, Is.True);
                    Assert.That(joined, Is.Not.Null);
                    Assert.That(roundToken.IsCancellationRequested, Is.False);

                    firstCancellation.Cancel();
                    yield return Finish(first);
                    Assert.That(first.IsCanceled, Is.True);
                    Assert.That(joined.IsCompleted, Is.False);
                    Assert.That(roundToken.IsCancellationRequested, Is.False,
                        "回放内重入加入的请求仍在等待，前两位退出不能取消执行。");
                    joinedCancellation.Cancel();
                    yield return Finish(joined);
                    Assert.That(joined.IsCanceled, Is.True);
                    Assert.That(roundToken.IsCancellationRequested, Is.True);
                    Assert.That(backendCancellations, Is.EqualTo(1));
                }
                finally
                {
                    firstCancellation.Cancel();
                    replayCancellation.Cancel();
                    joinedCancellation.Cancel();
                    _sharedShutdowns.Add(shared.StopAsync());
                    if (first != null && first.IsFaulted) _ = first.Exception;
                    if (replay != null && replay.IsFaulted) _ = replay.Exception;
                    if (joined != null && joined.IsFaulted) _ = joined.Exception;
                }
            }
        }

        [UnityTearDown]
        public IEnumerator ReleaseInterleavingRequests()
        {
            yield return Await(Task.WhenAll(_sharedShutdowns));
            _sharedShutdowns.Clear();
            foreach (var loader in _loaders) loader.CancelPending();
            var all = Task.WhenAll(_requests);
            try
            {
                yield return Finish(all);
                // Tests assert their outcomes; cleanup observes expected faults without rethrowing them.
                if (all.IsFaulted) all.Exception.Handle(_ => true);
            }
            finally
            {
                foreach (var request in _requests)
                    if (request.Status == TaskStatus.RanToCompletion) request.Result.Dispose();
                _requests.Clear();
                _loaders.Clear();
            }
            // Base teardown runs afterwards and can unload every product whose successful request returned.
        }

        private ControlledLoader CreateLoader(bool observeCancellation)
        {
            var loader = new ControlledLoader(observeCancellation);
            _loaders.Add(loader);
            return loader;
        }

        private Task<ResourceRef<string>> TrackRequest(Task<ResourceRef<string>> request)
        {
            _requests.Add(request);
            return request;
        }

        private RS CreateSystem(ControlledLoader loader, int concurrency)
            => Track(new RS(Options(loader, concurrency)));

        private static ResourceSystemOptions Options(ControlledLoader loader, int concurrency)
            => new ResourceSystemOptions { ExecutionContext = new UnityExecutionContext() }
                .AddLoader(ResourceSource.Local, loader, new LoaderPolicy(concurrency, null));

        private sealed class ControlledLoader : ILoader
        {
            private readonly bool _observeCancellation;
            private readonly object _gate = new object();
            private readonly List<PendingLoad> _loads = new List<PendingLoad>();
            private int _active;
            private int _peak;
            private int _order;
            private bool _cleaningUp;

            public ControlledLoader(bool observeCancellation) { _observeCancellation = observeCancellation; }
            public int Count { get { lock (_gate) return _loads.Count; } }
            public int PeakActive { get { lock (_gate) return _peak; } }
            public string[] Addresses { get { lock (_gate) return _loads.Select(load => load.Key.Address).ToArray(); } }
            public PendingLoad this[int index] { get { lock (_gate) return _loads[index]; } }

            public async Task<IResource> LoadAsync(ResourceKey key, IExecutionContext context,
                IProgress<OperationProgress> progress, CancellationToken cancellation)
            {
                var pending = new PendingLoad(key, cancellation, progress);
                using (_observeCancellation ? cancellation.Register(pending.Cancel) : default(CancellationTokenRegistration))
                {
                    lock (_gate)
                    {
                        pending.EnterOrder = ++_order;
                        _loads.Add(pending);
                        _peak = Math.Max(_peak, ++_active);
                        if (_cleaningUp) pending.Cancel();
                    }
                    try
                    {
                        // Every actual load traverses the real Unity execution context before its gate.
                        await context.RunAsync(() => { }).ConfigureAwait(false);
                        return await pending.Completion.Task.ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _active--;
                            pending.ExitOrder = ++_order;
                        }
                    }
                }
            }

            public void CancelPending()
            {
                PendingLoad[] pending;
                lock (_gate)
                {
                    // Also release work that was queued and only reaches the backend during teardown.
                    _cleaningUp = true;
                    pending = _loads.ToArray();
                }
                foreach (var load in pending) load.Cancel();
            }
        }

        private sealed class PendingLoad
        {
            public readonly ResourceKey Key;
            public readonly CancellationToken Token;
            public readonly TaskCompletionSource<IResource> Completion =
                new TaskCompletionSource<IResource>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly IProgress<OperationProgress> _progress;
            public int EnterOrder;
            public int ExitOrder;
            public bool HasExited => Volatile.Read(ref ExitOrder) != 0;

            public PendingLoad(ResourceKey key, CancellationToken token, IProgress<OperationProgress> progress)
            {
                Key = key;
                Token = token;
                _progress = progress;
            }
            public void Complete(IResource product) => Completion.TrySetResult(product);
            public void Fail(Exception error) => Completion.TrySetException(error);
            public void Cancel() => Completion.TrySetCanceled();
            public void Report(double fraction) => _progress.Report(new OperationProgress(fraction));
        }

        private sealed class SyncProgress : IProgress<OperationProgress>
        {
            private readonly Action<OperationProgress> _report;
            public SyncProgress(Action<OperationProgress> report) { _report = report; }
            public void Report(OperationProgress value) => _report(value);
        }

        private sealed class TrackedProduct : IResource
        {
            private int _releaseCount;
            public TrackedProduct(string value) { Value = value; }
            public object Value { get; }
            public int ReleaseCount => Volatile.Read(ref _releaseCount);
            public int ReleaseThread { get; private set; }
            public void Release()
            {
                ReleaseThread = Thread.CurrentThread.ManagedThreadId;
                Interlocked.Increment(ref _releaseCount);
            }
        }
    }
}
