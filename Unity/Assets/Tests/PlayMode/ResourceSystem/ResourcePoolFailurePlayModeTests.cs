using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.TestTools;
using UnityToolKit.Runtime.ResourceSystem;
using CoreResourceSystem = ToolKit.Tools.Common.ResourceSystem;

namespace Tests.PlayMode.ResourceSystem
{
    public class ResourcePoolFailurePlayModeTests : ResourcePlayModeTestBase
    {
        private readonly List<PrototypeRequest> _requests = new List<PrototypeRequest>();
        private readonly List<FaultingGameObjectFactory> _factories = new List<FaultingGameObjectFactory>();
        private readonly List<Task<InstanceLease<GameObject>>> _rentals = new List<Task<InstanceLease<GameObject>>>();

        // 派生类先结束测试持有的门闩与租约，再由基类关闭系统和销毁 Unity 对象。
        [UnityTearDown]
        public IEnumerator FinishPendingRentals()
        {
            foreach (var factory in _factories) factory.ResetErrors();
            foreach (var request in _requests) request.Ready.TrySetResult(true);
            var pending = Task.WhenAll(_rentals);
            try { yield return Finish(pending); }
            finally
            {
                foreach (var rental in _rentals)
                {
                    if (rental.Status == TaskStatus.RanToCompletion) rental.Result.Dispose();
                    else if (rental.IsFaulted) _ = rental.Exception;
                }
                _ = pending.Exception; // 预期失败已经由测试断言，清理阶段只观察异常。
                _rentals.Clear();
                _requests.Clear();
                _factories.Clear();
            }
            yield return null; // 归还可能向主线程投递；先让这些回调结束。
        }

        [UnityTest]
        public IEnumerator CancellingOneRentWaitingForSharedPrototypeDoesNotCancelOtherRent()
        {
            var request = Request("shared", wait: true);
            var system = CreateSystem(out var factory, request);
            yield return Await(system.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelled = Rent(system, "shared", cancellation.Token);
                yield return Await(request.Started.Task);
                var surviving = Rent(system, "shared");
                yield return null;
                Assert.That(surviving.IsCompleted, Is.False);
                Assert.That(request.Loads, Is.EqualTo(1));

                cancellation.Cancel();
                yield return Finish(cancelled);
                Assert.That(cancelled.IsCanceled, Is.True);
                Assert.That(surviving.IsCompleted, Is.False);
                Assert.That(request.RoundToken.IsCancellationRequested, Is.False,
                    "仍有另一个租用等待原型时，不得取消共享加载。");
                request.Ready.TrySetResult(true);
                yield return Await(surviving);
                Assert.That(surviving.Result.Value.activeSelf, Is.True);
                Assert.That(factory.Created.Count, Is.EqualTo(1), "被取消的等待者不能生成无人持有的实例。");
                Assert.That(Holds(system, "shared"), Is.EqualTo(1));
                Assert.That(system.Snapshot().Pools.Single().Active, Is.EqualTo(1));
            }
        }

        [UnityTest]
        public IEnumerator CancelledPrototypeAIsReleasedLateWithoutAffectingSuccessfulPrototypeB()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var a = Request("A", wait: true);
            var b = Request("B");
            var system = CreateSystem(out var factory, a, b);
            yield return Await(system.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelled = Rent(system, "A", cancellation.Token);
                yield return Await(a.Started.Task);
                cancellation.Cancel();
                yield return Finish(cancelled);
                Assert.That(cancelled.IsCanceled, Is.True);
                yield return Await(a.Cancelled.Task);

                var successful = Rent(system, "B");
                yield return Await(successful);
                var instanceB = successful.Result.Value;
                Assert.That(instanceB.activeSelf, Is.True);
                Assert.That(factory.Created.Count, Is.EqualTo(1));
                Assert.That(a.Released, Is.Zero, "A 后端此时仍持有尚未交出的原型产物。");

                // 后端故意忽略取消并迟到交付，清理责任必须由 A 原来的加载轮承担。
                a.Ready.TrySetResult(true);
                yield return Until(() => Volatile.Read(ref a.Released) == 1);
                Assert.That(a.ReleaseThread, Is.EqualTo(mainThread));
                Assert.That(system.Snapshot().Entries.Any(entry => entry.Key.Address == "A"), Is.False);
                Assert.That(successful.Result.IsValid, Is.True);
                Assert.That(successful.Result.Value, Is.SameAs(instanceB));
                Assert.That(instanceB.activeSelf, Is.True);
                Assert.That(Holds(system, "B"), Is.EqualTo(1));
                Assert.That(b.Released, Is.Zero);
                Assert.That(factory.Created.Count, Is.EqualTo(1), "迟到 A 不能误创建实例或挪用 B 的实例。");
            }
        }

        [UnityTest]
        public IEnumerator CreateAndOnRentFailuresDestroyUnusableInstancesAndAllowRecovery()
        {
            var request = Request("prefab");
            var system = CreateSystem(out var factory, request);
            yield return Await(system.InitializeAsync());
            factory.CreateError = new InvalidOperationException("injected create failure");
            var failedCreate = Rent(system, "prefab");
            yield return Finish(failedCreate);
            AssertFailure(failedCreate, DiagnosticCodes.InstanceCreateFailed, factory.CreateError);
            Assert.That(factory.Created, Is.Empty);
            AssertPoolEmpty(system);

            factory.CreateError = null;
            factory.RentError = new InvalidOperationException("injected first rent failure");
            var failedFirstRent = Rent(system, "prefab");
            yield return Finish(failedFirstRent);
            AssertFailure(failedFirstRent, DiagnosticCodes.InstanceCreateFailed, factory.RentError);
            Assert.That(factory.Created.Count, Is.EqualTo(1));
            var rejectedNew = factory.Created[0];
            yield return Until(() => rejectedNew == null);
            AssertPoolEmpty(system);

            factory.RentError = null;
            var good = Rent(system, "prefab");
            yield return Await(good);
            var reusedObject = good.Result.Value;
            good.Result.Dispose();
            Assert.That(reusedObject.activeSelf, Is.False);
            factory.RentError = new InvalidOperationException("injected reuse failure");
            var failedReuse = Rent(system, "prefab");
            yield return Finish(failedReuse);
            AssertFailure(failedReuse, DiagnosticCodes.InstanceResetFailed, factory.RentError);
            Assert.That(factory.Created.Count, Is.EqualTo(2), "复用失败不能偷偷创建替代实例。");
            yield return Until(() => reusedObject == null);
            AssertPoolEmpty(system);

            factory.RentError = null;
            var recovered = Rent(system, "prefab");
            yield return Await(recovered);
            Assert.That(recovered.Result.Value.activeSelf, Is.True);
            Assert.That(factory.Created.Count, Is.EqualTo(3));
            Assert.That(request.Loads, Is.EqualTo(1), "实例工厂失败不会重复读取仍由桶持有的原型。");
            Assert.That(Holds(system, "prefab"), Is.EqualTo(1));
            Assert.That(system.Snapshot().InstanceResidues, Is.Empty);
        }

        [UnityTest]
        public IEnumerator OnReturnFailureDestroysInstanceInsteadOfPuttingItBackIntoIdlePool()
        {
            var request = Request("prefab");
            var system = CreateSystem(out var factory, request);
            yield return Await(system.InitializeAsync());
            var renting = Rent(system, "prefab");
            yield return Await(renting);
            var failedObject = renting.Result.Value;
            factory.ReturnError = new InvalidOperationException("injected return failure");
            yield return Await(Task.Run(() => renting.Result.Dispose()));
            yield return Until(() => failedObject == null);
            Assert.That(factory.ReturnFailures, Is.EqualTo(1));
            Assert.That(factory.DestroyAttempts, Is.EqualTo(1));
            Assert.That(renting.Result.IsValid, Is.False);
            AssertPoolEmpty(system);
            Assert.That(system.Snapshot().InstanceResidues, Is.Empty);
            Assert.That(Holds(system, "prefab"), Is.EqualTo(1));

            factory.ReturnError = null;
            var next = Rent(system, "prefab");
            yield return Await(next);
            Assert.That(next.Result.Value.activeSelf, Is.True);
            Assert.That(factory.Created.Count, Is.EqualTo(2));
            Assert.That(request.Released, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DestroyFailureKeepsInstanceAndPrototypeUntilAnotherClearSucceeds()
        {
            var request = Request("prefab");
            var system = CreateSystem(out var factory, request);
            yield return Await(system.InitializeAsync());
            var renting = Rent(system, "prefab");
            yield return Await(renting);
            var instance = renting.Result.Value;
            renting.Result.Dispose();
            var failure = new InvalidOperationException("injected destroy failure");
            factory.DestroyError = failure;

            var firstClear = system.ClearPoolAsync();
            yield return Await(firstClear);
            Assert.That(firstClear.Result.Destroyed, Is.Zero);
            Assert.That(firstClear.Result.Failed.Count, Is.EqualTo(1));
            Assert.That(firstClear.Result.Failed[0].Instance, Is.SameAs(instance));
            Assert.That(firstClear.Result.Failed[0].Error, Is.SameAs(failure));
            Assert.That(instance != null, Is.True);
            Assert.That(system.Snapshot().InstanceResidues.Count, Is.EqualTo(1));
            Assert.That(system.Snapshot().Pools.Single().Closed, Is.True);
            Assert.That(Holds(system, "prefab"), Is.EqualTo(1));

            var unloading = system.UnloadAsync();
            yield return Await(unloading);
            Assert.That(unloading.Result.Released, Is.Empty);
            Assert.That(unloading.Result.SkippedInUse.Select(key => key.Address), Is.EquivalentTo(new[] { "prefab" }));
            Assert.That(request.Released, Is.Zero, "销毁失败的实例仍依赖原型，不能提前释放。");

            factory.DestroyError = null;
            var retry = system.ClearPoolAsync();
            yield return Await(retry);
            Assert.That(retry.Result.Destroyed, Is.EqualTo(1));
            Assert.That(retry.Result.Failed, Is.Empty);
            yield return Until(() => instance == null);
            Assert.That(factory.DestroyAttempts, Is.EqualTo(2));
            Assert.That(system.Snapshot().InstanceResidues, Is.Empty);
            Assert.That(system.Snapshot().Pools, Is.Empty);
            Assert.That(Holds(system, "prefab"), Is.Zero);
            var released = system.UnloadAsync();
            yield return Await(released);
            Assert.That(released.Result.Released.Select(key => key.Address), Is.EquivalentTo(new[] { "prefab" }));
            Assert.That(request.Released, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator SelectiveClearReleasesOnlyAWhileBRemainsUsableAndRetainsItsPrototype()
        {
            var a = Request("A");
            var b = Request("B");
            var system = CreateSystem(out _, a, b);
            yield return Await(system.InitializeAsync());
            var rentingA = Rent(system, "A");
            var rentingB = Rent(system, "B");
            yield return Await(Task.WhenAll(rentingA, rentingB));
            var instanceA = rentingA.Result.Value;
            var instanceB = rentingB.Result.Value;
            rentingA.Result.Dispose();

            var clearingA = system.ClearPoolAsync(key => key.Address == "A");
            yield return Await(clearingA);
            Assert.That(clearingA.Result.Destroyed, Is.EqualTo(1));
            Assert.That(clearingA.Result.StillActive, Is.Zero, "清池统计也必须限于选择器选中的原型。");
            Assert.That(clearingA.Result.Failed, Is.Empty);
            yield return Until(() => instanceA == null);
            Assert.That(rentingB.Result.IsValid, Is.True);
            Assert.That(rentingB.Result.Value, Is.SameAs(instanceB));
            Assert.That(instanceB.activeSelf, Is.True);
            Assert.That(system.Snapshot().Pools.Single().PrototypeKey.Address, Is.EqualTo("B"));

            var unloading = system.UnloadAsync();
            yield return Await(unloading);
            Assert.That(unloading.Result.Released.Select(key => key.Address), Is.EquivalentTo(new[] { "A" }));
            Assert.That(unloading.Result.SkippedInUse.Select(key => key.Address), Is.EquivalentTo(new[] { "B" }));
            Assert.That(a.Released, Is.EqualTo(1));
            Assert.That(b.Released, Is.Zero);
            Assert.That(Holds(system, "B"), Is.EqualTo(1));

            rentingB.Result.Dispose();
            var reusedB = Rent(system, "B");
            yield return Await(reusedB);
            Assert.That(reusedB.Result.Value, Is.SameAs(instanceB));
            Assert.That(instanceB.activeSelf, Is.True);
            Assert.That(b.Loads, Is.EqualTo(1));
        }

        private PrototypeRequest Request(string address, bool wait = false)
        {
            var prototype = TrackObject(new GameObject("prototype-" + address));
            prototype.SetActive(false);
            var request = new PrototypeRequest(address, prototype);
            if (!wait) request.Ready.TrySetResult(true);
            _requests.Add(request);
            return request;
        }

        private CoreResourceSystem CreateSystem(out FaultingGameObjectFactory factory, params PrototypeRequest[] requests)
        {
            factory = new FaultingGameObjectFactory(instance => TrackObject(instance));
            _factories.Add(factory);
            return Track(new CoreResourceSystem(new ResourceSystemOptions
            {
                ExecutionContext = new UnityExecutionContext(),
                InstanceFactory = factory,
                Pool = new PoolPolicy(2)
            }.AddLoader(ResourceSource.Local, new ControlledPrototypeLoader(requests), new LoaderPolicy(2, null))));
        }

        private Task<InstanceLease<GameObject>> Rent(CoreResourceSystem system, string address, CancellationToken token = default)
        {
            var rental = system.RentAsync<GameObject>(ResourceSource.Local, address, token);
            _rentals.Add(rental);
            return rental;
        }

        private static int Holds(CoreResourceSystem system, string address)
            => system.Snapshot().Entries.Single(entry => entry.Key.Address == address).Holds;

        private static void AssertPoolEmpty(CoreResourceSystem system)
        {
            var bucket = system.Snapshot().Pools.Single();
            Assert.That(bucket.Active, Is.Zero);
            Assert.That(bucket.Creating, Is.Zero);
            Assert.That(bucket.Idle, Is.Zero);
        }

        private static void AssertFailure(Task task, string diagnostic, Exception cause)
        {
            Assert.That(task.IsFaulted, Is.True);
            var error = task.Exception.InnerException as ResourceException;
            Assert.That(error, Is.Not.Null);
            Assert.That(error.ResourceError.DiagnosticCode, Is.EqualTo(diagnostic));
            Assert.That(error.ResourceError.Cause, Is.SameAs(cause));
        }

        private sealed class PrototypeRequest
        {
            public readonly string Address;
            public readonly GameObject Prototype;
            public readonly TaskCompletionSource<bool> Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationToken RoundToken;
            public int Loads;
            public int Released;
            public int ReleaseThread;

            public PrototypeRequest(string address, GameObject prototype)
            {
                Address = address;
                Prototype = prototype;
            }
        }

        private sealed class ControlledPrototypeLoader : ILoader
        {
            private readonly Dictionary<string, PrototypeRequest> _requests;
            public ControlledPrototypeLoader(IEnumerable<PrototypeRequest> requests)
                => _requests = requests.ToDictionary(request => request.Address);

            public async Task<IResource> LoadAsync(ResourceKey key, IExecutionContext context,
                IProgress<OperationProgress> progress, CancellationToken cancellation)
            {
                var request = _requests[key.Address];
                request.RoundToken = cancellation;
                Interlocked.Increment(ref request.Loads);
                request.Started.TrySetResult(true);
                // 特意允许取消后仍返回，用于验证资源系统处理非协作后端的迟到产物。
                using (cancellation.Register(() => request.Cancelled.TrySetResult(true)))
                    await request.Ready.Task.ConfigureAwait(false);
                return new Product(request.Prototype, () =>
                {
                    request.ReleaseThread = Thread.CurrentThread.ManagedThreadId;
                    Interlocked.Increment(ref request.Released);
                });
            }
        }

        private sealed class FaultingGameObjectFactory : IInstanceFactory
        {
            private readonly GameObjectFactory _inner = new GameObjectFactory();
            private readonly Action<GameObject> _track;
            public readonly List<GameObject> Created = new List<GameObject>();
            public Exception CreateError;
            public Exception RentError;
            public Exception ReturnError;
            public Exception DestroyError;
            public int ReturnFailures;
            public int DestroyAttempts;

            public FaultingGameObjectFactory(Action<GameObject> track) => _track = track;
            public Type PrototypeType => _inner.PrototypeType;
            public object Create(object prototype)
            {
                if (CreateError != null) throw CreateError;
                var instance = (GameObject)_inner.Create(prototype);
                Created.Add(instance);
                _track(instance);
                return instance;
            }
            public void OnRent(object instance)
            {
                if (RentError != null) throw RentError;
                _inner.OnRent(instance);
            }
            public void OnReturn(object instance)
            {
                if (ReturnError != null) { ReturnFailures++; throw ReturnError; }
                _inner.OnReturn(instance);
            }
            public void Destroy(object instance)
            {
                DestroyAttempts++;
                if (DestroyError != null) throw DestroyError;
                _inner.Destroy(instance);
            }
            public void ResetErrors() => CreateError = RentError = ReturnError = DestroyError = null;
        }
    }
}
