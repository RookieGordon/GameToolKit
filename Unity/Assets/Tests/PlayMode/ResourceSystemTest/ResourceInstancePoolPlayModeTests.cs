using System;
using System.Collections;
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
    public class ResourceInstancePoolPlayModeTests : ResourcePlayModeTestBase
    {
        [UnityTest]
        public IEnumerator WorkerRentAndReturnActivateDeactivateAndReuseRealGameObject()
        {
            var idleParent = TrackObject(new GameObject("idle-parent"));
            var loader = new PrototypeLoader(CreatePrototype());
            var system = CreateSystem(loader, 2, new GameObjectFactory(idleParent.transform));
            yield return Await(system.InitializeAsync());
            InstanceLease<GameObject> first = null;
            InstanceLease<GameObject> reused = null;
            try
            {
                var renting = Task.Run(() => system.RentAsync<GameObject>(ResourceSource.Local, "prefab"));
                yield return Await(renting);
                first = renting.Result;
                var instance = TrackObject(first.Value);
                Assert.That(instance.activeSelf, Is.True);
                Assert.That(loader.Loads, Is.EqualTo(1));
                Assert.That(PrototypeHolds(system), Is.EqualTo(1));

                yield return Await(Task.Run(() => { first.Dispose(); first.Dispose(); }));
                Assert.That(first.IsValid, Is.False);
                Assert.That(Assert.Throws<ResourceException>(() => { _ = first.Value; }).ResourceError.DiagnosticCode,
                    Is.EqualTo(DiagnosticCodes.AssetInvalidated));
                yield return Until(() => !instance.activeSelf && SingleBucket(system).Idle == 1);
                Assert.That(instance.transform.parent, Is.EqualTo(idleParent.transform));
                Assert.That(SingleBucket(system).Active, Is.Zero, "重复归还不能再次修改池的计数。");

                var rerenting = Task.Run(() => system.RentAsync<GameObject>(ResourceSource.Local, "prefab"));
                yield return Await(rerenting);
                reused = rerenting.Result;
                Assert.That(reused.Value, Is.SameAs(instance));
                Assert.That(reused.Value.activeSelf, Is.True);
                Assert.That(first.IsValid, Is.False, "新租约不能恢复旧租约的有效性。");
                Assert.That(loader.Loads, Is.EqualTo(1));
                Assert.That(SingleBucket(system).Active, Is.EqualTo(1));
            }
            finally { first?.Dispose(); reused?.Dispose(); }
        }

        [UnityTest]
        public IEnumerator IdleCapacityEvictsOverflowWithoutLimitingActiveRentals()
        {
            var loader = new PrototypeLoader(CreatePrototype());
            var system = CreateSystem(loader, 1);
            yield return Await(system.InitializeAsync());
            InstanceLease<GameObject> first = null;
            InstanceLease<GameObject> second = null;
            try
            {
                var firstTask = system.RentAsync<GameObject>(ResourceSource.Local, "prefab");
                var secondTask = system.RentAsync<GameObject>(ResourceSource.Local, "prefab");
                yield return Await(Task.WhenAll(firstTask, secondTask));
                first = firstTask.Result;
                second = secondTask.Result;
                var kept = TrackObject(first.Value);
                var overflow = TrackObject(second.Value);
                Assert.That(kept, Is.Not.SameAs(overflow));
                Assert.That(SingleBucket(system).Active, Is.EqualTo(2), "容量仅限制闲置实例，不限制活跃租约。");
                Assert.That(loader.Loads, Is.EqualTo(1));
                Assert.That(PrototypeHolds(system), Is.EqualTo(1), "同桶共享一份原型持有。");

                first.Dispose();
                second.Dispose();
                yield return Until(() => overflow == null);
                Assert.That(kept != null, Is.True);
                Assert.That(kept.activeSelf, Is.False);
                Assert.That(SingleBucket(system).Idle, Is.EqualTo(1));
                Assert.That(SingleBucket(system).Active, Is.Zero);
            }
            finally { first?.Dispose(); second?.Dispose(); }
        }

        [UnityTest]
        public IEnumerator ZeroIdleCapacityDestroysReturnedObjectButRetainsOpenBucketsPrototype()
        {
            var loader = new PrototypeLoader(CreatePrototype());
            var system = CreateSystem(loader, 0);
            yield return Await(system.InitializeAsync());
            InstanceLease<GameObject> lease = null;
            try
            {
                var renting = system.RentAsync<GameObject>(ResourceSource.Local, "prefab");
                yield return Await(renting);
                lease = renting.Result;
                var instance = TrackObject(lease.Value);
                lease.Dispose();
                yield return Until(() => instance == null);
                Assert.That(SingleBucket(system).Idle, Is.Zero);
                Assert.That(PrototypeHolds(system), Is.EqualTo(1));
                var unloading = system.UnloadAsync();
                yield return Await(unloading);
                Assert.That(unloading.Result.SkippedInUse.Count, Is.EqualTo(1));
                Assert.That(loader.Released, Is.Zero);
            }
            finally { lease?.Dispose(); }
        }

        [UnityTest]
        public IEnumerator ClearDestroysIdleAndKeepsBusyLeaseUntilReturnThenReleasesPrototypeHold()
        {
            var loader = new PrototypeLoader(CreatePrototype());
            var system = CreateSystem(loader, 2);
            yield return Await(system.InitializeAsync());
            InstanceLease<GameObject> busy = null;
            InstanceLease<GameObject> idle = null;
            try
            {
                var busyTask = system.RentAsync<GameObject>(ResourceSource.Local, "prefab");
                var idleTask = system.RentAsync<GameObject>(ResourceSource.Local, "prefab");
                yield return Await(Task.WhenAll(busyTask, idleTask));
                busy = busyTask.Result;
                idle = idleTask.Result;
                var busyObject = TrackObject(busy.Value);
                var idleObject = TrackObject(idle.Value);
                idle.Dispose();

                var clearing = Task.Run(() => system.ClearPoolAsync());
                yield return Await(clearing);
                Assert.That(clearing.Result.Destroyed, Is.EqualTo(1));
                Assert.That(clearing.Result.StillActive, Is.EqualTo(1));
                Assert.That(clearing.Result.Failed, Is.Empty);
                yield return Until(() => idleObject == null);
                Assert.That(busy.IsValid, Is.True);
                Assert.That(busy.Value, Is.SameAs(busyObject));
                Assert.That(busyObject.activeSelf, Is.True);
                Assert.That(SingleBucket(system).Closed, Is.True);
                Assert.That(PrototypeHolds(system), Is.EqualTo(1));

                yield return Await(Task.Run(() => busy.Dispose()));
                yield return Until(() => busyObject == null && system.Snapshot().Pools.Count == 0);
                Assert.That(PrototypeHolds(system), Is.Zero);
                var unloading = system.UnloadAsync();
                yield return Await(unloading);
                Assert.That(unloading.Result.Released.Count, Is.EqualTo(1));
                Assert.That(loader.Released, Is.EqualTo(1));
            }
            finally { busy?.Dispose(); idle?.Dispose(); }
        }

        [UnityTest]
        public IEnumerator RentAfterClearUsesNewBucketWhileOldLeaseRemainsValid()
        {
            var loader = new PrototypeLoader(CreatePrototype());
            var system = CreateSystem(loader, 1);
            yield return Await(system.InitializeAsync());
            InstanceLease<GameObject> old = null;
            InstanceLease<GameObject> current = null;
            try
            {
                var renting = system.RentAsync<GameObject>(ResourceSource.Local, "prefab");
                yield return Await(renting);
                old = renting.Result;
                var oldObject = TrackObject(old.Value);
                yield return Await(system.ClearPoolAsync());
                var newRent = system.RentAsync<GameObject>(ResourceSource.Local, "prefab");
                yield return Await(newRent);
                current = newRent.Result;
                var newObject = TrackObject(current.Value);
                Assert.That(newObject, Is.Not.SameAs(oldObject));
                Assert.That(system.Snapshot().Pools.Count, Is.EqualTo(2));
                Assert.That(PrototypeHolds(system), Is.EqualTo(2));
                Assert.That(loader.Loads, Is.EqualTo(1));

                old.Dispose();
                current.Dispose();
                yield return Until(() => oldObject == null && system.Snapshot().Pools.Count == 1);
                Assert.That(newObject != null, Is.True);
                Assert.That(newObject.activeSelf, Is.False);
                Assert.That(SingleBucket(system).Closed, Is.False);
                Assert.That(SingleBucket(system).Idle, Is.EqualTo(1));
                Assert.That(PrototypeHolds(system), Is.EqualTo(1));
            }
            finally { old?.Dispose(); current?.Dispose(); }
        }

        [UnityTest]
        public IEnumerator ClearWhilePrototypeIsPendingRejectsUnadmittedRentWithoutCreatingInstance()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loader = new PrototypeLoader(CreatePrototype(), gate);
            var system = CreateSystem(loader, 1);
            yield return Await(system.InitializeAsync());
            var renting = system.RentAsync<GameObject>(ResourceSource.Local, "prefab");
            try
            {
                yield return Await(loader.Started.Task);
                var clearing = system.ClearPoolAsync();
                yield return Await(clearing);
                Assert.That(clearing.Result.Destroyed, Is.Zero);
                Assert.That(clearing.Result.StillActive, Is.Zero);
                gate.TrySetResult(true);
                yield return Finish(renting);
                Assert.That(renting.IsFaulted, Is.True);
                var error = renting.Exception.GetBaseException() as ResourceException;
                Assert.That(error, Is.Not.Null);
                Assert.That(error.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.InstancePoolClosed));
                Assert.That(system.Snapshot().Pools, Is.Empty);
                Assert.That(PrototypeHolds(system), Is.Zero);
            }
            finally
            {
                gate.TrySetResult(true);
                if (renting.Status == TaskStatus.RanToCompletion) renting.Result.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator CancellationDuringNativeCreationDestroysTheUnclaimedGameObject()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var factory = new CancelOnCreateFactory(cancellation);
                var system = CreateSystem(new PrototypeLoader(CreatePrototype()), 1, factory);
                yield return Await(system.InitializeAsync());
                var renting = system.RentAsync<GameObject>(ResourceSource.Local, "prefab", cancellation.Token);
                try
                {
                    yield return Finish(renting);
                    Assert.That(renting.IsCanceled, Is.True);
                    Assert.That(ReferenceEquals(factory.Created, null), Is.False, "确实创建过真实 Unity 对象，才可检验迟到清理。");
                    yield return Until(() => factory.Created == null);
                    var bucket = SingleBucket(system);
                    Assert.That(bucket.Active, Is.Zero);
                    Assert.That(bucket.Creating, Is.Zero);
                    Assert.That(bucket.Idle, Is.Zero);
                }
                finally
                {
                    if (renting.Status == TaskStatus.RanToCompletion) renting.Result.Dispose();
                    if (!ReferenceEquals(factory.Created, null)) TrackObject(factory.Created);
                }
            }
        }

        [UnityTest]
        public IEnumerator DisposeClosesPoolWithoutInvalidatingBusyLeaseAndLateReturnDestroysInstance()
        {
            var loader = new PrototypeLoader(CreatePrototype());
            var system = CreateSystem(loader, 2);
            yield return Await(system.InitializeAsync());
            InstanceLease<GameObject> busy = null;
            InstanceLease<GameObject> idle = null;
            try
            {
                var busyTask = system.RentAsync<GameObject>(ResourceSource.Local, "prefab");
                var idleTask = system.RentAsync<GameObject>(ResourceSource.Local, "prefab");
                yield return Await(Task.WhenAll(busyTask, idleTask));
                busy = busyTask.Result;
                idle = idleTask.Result;
                var busyObject = TrackObject(busy.Value);
                var idleObject = TrackObject(idle.Value);
                idle.Dispose();
                yield return Await(system.DisposeAsync().AsTask());
                yield return Until(() => idleObject == null);

                Assert.That(system.IsDisposed, Is.True);
                Assert.That(busy.IsValid, Is.True);
                Assert.That(busy.Value, Is.SameAs(busyObject));
                Assert.That(busyObject.activeSelf, Is.True);
                Assert.That(loader.Released, Is.Zero, "忙碌实例仍依赖原型，退出不能提前释放。");
                Assert.That(Assert.Throws<ResourceException>(() =>
                    system.RentAsync<GameObject>(ResourceSource.Local, "prefab")).ResourceError.DiagnosticCode,
                    Is.EqualTo(DiagnosticCodes.LifecycleSystemDisposed));

                yield return Await(Task.Run(() => busy.Dispose()));
                yield return Until(() => busyObject == null && system.Snapshot().Pools.Count == 0);
                Assert.That(busy.IsValid, Is.False);
                Assert.That(PrototypeHolds(system), Is.Zero);
                yield return Await(system.DisposeAsync().AsTask());
            }
            finally { busy?.Dispose(); idle?.Dispose(); }
        }

        private GameObject CreatePrototype()
        {
            var prototype = TrackObject(new GameObject("pool-prototype"));
            prototype.SetActive(false);
            return prototype;
        }

        private CoreResourceSystem CreateSystem(ILoader loader, int maxIdle, IInstanceFactory factory = null)
        {
            return Track(new CoreResourceSystem(new ResourceSystemOptions
            {
                ExecutionContext = new UnityExecutionContext(),
                InstanceFactory = factory ?? new GameObjectFactory(),
                Pool = new PoolPolicy(maxIdle)
            }.AddLoader(ResourceSource.Local, loader, new LoaderPolicy(4, null))));
        }

        private static PoolBucketInfo SingleBucket(CoreResourceSystem system)
        {
            var pools = system.Snapshot().Pools;
            Assert.That(pools.Count, Is.EqualTo(1));
            return pools[0];
        }

        private static int PrototypeHolds(CoreResourceSystem system)
        {
            var entries = system.Snapshot().Entries;
            Assert.That(entries.Count, Is.EqualTo(1));
            return entries[0].Holds;
        }

        private sealed class PrototypeLoader : ILoader
        {
            private readonly GameObject _prototype;
            private readonly TaskCompletionSource<bool> _gate;
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Loads;
            public int Released;

            public PrototypeLoader(GameObject prototype, TaskCompletionSource<bool> gate = null)
            {
                _prototype = prototype;
                _gate = gate;
            }

            public async Task<IResource> LoadAsync(ResourceKey key,
                IProgress<OperationProgress> progress, CancellationToken cancellation)
            {
                Interlocked.Increment(ref Loads);
                Started.TrySetResult(true);
                if (_gate != null)
                {
                    using (cancellation.Register(() => _gate.TrySetCanceled()))
                        await _gate.Task.ConfigureAwait(false);
                }
                cancellation.ThrowIfCancellationRequested();
                // 原型由测试拥有；产物释放仅记录所有权归还，不销毁测试的原型对象。
                return new Product(_prototype, () => Interlocked.Increment(ref Released));
            }
        }

        private sealed class CancelOnCreateFactory : IInstanceFactory
        {
            private readonly GameObjectFactory _inner = new GameObjectFactory();
            private readonly CancellationTokenSource _cancellation;
            public GameObject Created;
            public CancelOnCreateFactory(CancellationTokenSource cancellation) => _cancellation = cancellation;
            public Type PrototypeType => _inner.PrototypeType;
            public object Create(object prototype)
            {
                Created = (GameObject)_inner.Create(prototype);
                _cancellation.Cancel();
                return Created;
            }
            public void OnRent(object instance) => _inner.OnRent(instance);
            public void OnReturn(object instance) => _inner.OnReturn(instance);
            public void Destroy(object instance) => _inner.Destroy(instance);
        }
    }
}
