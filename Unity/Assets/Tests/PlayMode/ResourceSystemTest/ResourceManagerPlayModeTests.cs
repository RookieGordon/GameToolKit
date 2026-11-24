using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.TestTools;
using UnityToolKit.Runtime.ResourceSystem;
using RS = ToolKit.Tools.Common.ResourceSystem;

namespace Tests.PlayMode.ResourceSystem
{
    public class ResourceManagerPlayModeTests : ResourcePlayModeTestBase
    {
        private ResourceSystemConfig NewConfig()
            => TrackObject(ScriptableObject.CreateInstance<ResourceSystemConfig>());

        private ResourceManager Assemble(ResourceSystemConfig config, out RS system)
        {
            var manager = TrackObject(new GameObject(nameof(ResourceManagerPlayModeTests)))
                .AddComponent<ResourceManager>();
            system = Track(new RS(config.ConvertToOptions()));
            // 只替换装配入口，后续走真实公开接口；不覆盖项目业务使用的 Resources 配置资产。
            var field = typeof(ResourceManager).GetField("_resSystem", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(manager, system);
            return manager;
        }

        [UnityTest]
        public IEnumerator ConfiguredExternalLoaderIsCreatedOnlyByItsOwnerAfterInitialization()
        {
            var config = NewConfig();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            config.Initializers.Add(new CallbackInitializer { Callback = _ => ready.Task });
            var deferred = new RejectCreationLoaderConfig
            {
                Source = ResourceSource.AssetBundle, InitializeOnStartup = false,
                MaxConcurrentLoads = 1, TimeoutSeconds = 0.2
            };
            config.Loaders.Add(deferred);
            config.Loaders.Add(new ResourcesLoaderConfig());
            var manager = Assemble(config, out _);
            var initialization = manager.InitializeAsync();
            try
            {
                yield return null;
                Assert.That(manager.IsInitialized, Is.False);
                Assert.That(manager.InitializeAsync(), Is.SameAs(initialization));
                Assert.Throws<InvalidOperationException>(() => manager.RegisterAssetBundleLoader(new ValueLoader("early")));
                var earlyLoad = manager.LoadAsync<string>(ResourceSource.AssetBundle, "early", null, CancellationToken.None);
                yield return Finish(earlyLoad);
                Assert.Throws<InvalidOperationException>(() => earlyLoad.GetAwaiter().GetResult());
            }
            finally { ready.TrySetResult(true); }
            yield return Await(initialization);

            var otherSource = manager.LoadAsync<TextAsset>(ResourceSource.Resources,
                "ResourceSystemPlayMode/SampleText", null, CancellationToken.None);
            yield return Await(otherSource);
            using (var reference = otherSource.Result)
                Assert.That(reference.Value.text.Trim(), Is.EqualTo("resource-system-playmode"));
            var missing = manager.LoadAsync<string>(ResourceSource.AssetBundle, "before", null, CancellationToken.None);
            yield return Finish(missing);
            AssertCode(missing, DiagnosticCodes.LoaderNotRegistered);

            deferred.TimeoutSeconds = 0; // 资产后续编辑不能改变装配时保存的策略。
            manager.RegisterAssetBundleLoader(new WaitingLoader());
            var timedOut = manager.LoadAsync<string>(ResourceSource.AssetBundle, "slow", null, CancellationToken.None);
            yield return Finish(timedOut);
            AssertCode(timedOut, DiagnosticCodes.RequestTimeout);
            manager.RegisterAssetBundleLoader(new ValueLoader("bundle-ready"), new LoaderPolicy(1, null));
            var loaded = manager.LoadAsync<string>(ResourceSource.AssetBundle, "ready", null, CancellationToken.None);
            yield return Await(loaded);
            using (var reference = loaded.Result) Assert.That(reference.Value, Is.EqualTo("bundle-ready"));
            Assert.That(manager.InitializeAsync(), Is.SameAs(initialization));
            Assert.That(manager.IsInitialized, Is.True);
        }

        [UnityTest]
        public IEnumerator DuplicateSourceConfigurationUsesTheLastWholeEntry()
        {
            var config = NewConfig();
            config.Loaders.Add(new ResourcesLoaderConfig());
            config.Loaders.Add(new LoaderConfig { Source = ResourceSource.Resources, InitializeOnStartup = false });
            var external = Assemble(config, out _);
            yield return Await(external.InitializeAsync());
            var missing = external.LoadAsync<TextAsset>(ResourceSource.Resources,
                "ResourceSystemPlayMode/SampleText", null, CancellationToken.None);
            yield return Finish(missing);
            AssertCode(missing, DiagnosticCodes.LoaderNotRegistered);

            config.Loaders.Reverse();
            var builtIn = Assemble(config, out _);
            yield return Await(builtIn.InitializeAsync());
            var loaded = builtIn.LoadAsync<TextAsset>(ResourceSource.Resources,
                "ResourceSystemPlayMode/SampleText", null, CancellationToken.None);
            yield return Await(loaded);
            using (var reference = loaded.Result)
                Assert.That(reference.Value.text.Trim(), Is.EqualTo("resource-system-playmode"));
        }

        [UnityTest]
        public IEnumerator FailedManagerInitializationCanBeRetriedWithoutReassembly()
        {
            var config = NewConfig();
            var attempts = 0;
            var expected = new InvalidOperationException("preparation failed");
            config.Initializers.Add(new CallbackInitializer
            {
                Callback = _ => ++attempts == 1 ? Task.FromException(expected) : Task.CompletedTask
            });
            var manager = Assemble(config, out _);
            var failed = manager.InitializeAsync();
            yield return Finish(failed);
            Assert.That(Assert.Throws<InvalidOperationException>(() => failed.GetAwaiter().GetResult()), Is.SameAs(expected));
            Assert.That(manager.IsInitialized, Is.False);
            Assert.Throws<InvalidOperationException>(() => manager.RegisterLoader(ResourceSource.Local, new ValueLoader("early")));
            yield return Await(manager.InitializeAsync());
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(manager.IsInitialized, Is.True);
        }

        [UnityTest]
        public IEnumerator LoadCoroutineDeliversReferenceAndUnloadCoroutineReportsOwnership()
        {
            var manager = Assemble(NewConfig(), out _);
            yield return Await(manager.InitializeAsync());
            manager.RegisterLoader(ResourceSource.Local, new ValueLoader("loaded"));
            ResourceRef<string> held = null;
            Exception failure = null;
            var callbackThread = 0;
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                yield return Run(manager, manager.LoadAsync<string>(ResourceSource.Local, "value",
                    onCompleted: value => { held = value; callbackThread = Thread.CurrentThread.ManagedThreadId; },
                    onError: error => failure = error));
                Assert.That(failure, Is.Null);
                Assert.That(held, Is.Not.Null);
                Assert.That(held.Value, Is.EqualTo("loaded"));
                Assert.That(callbackThread, Is.EqualTo(mainThread));
                UnloadResult inUse = null;
                yield return Run(manager, manager.UnloadAsync(onCompleted: value => inUse = value));
                Assert.That(inUse.SkippedInUse.Count, Is.EqualTo(1));
                held.Dispose();
                UnloadResult unloaded = null;
                yield return Run(manager, manager.UnloadAsync(onCompleted: value => unloaded = value));
                Assert.That(unloaded.Released.Count, Is.EqualTo(1));
            }
            finally { held?.Dispose(); }
        }

        [UnityTest]
        public IEnumerator LoadCoroutineSeparatesFailureAndCancellationCallbacks()
        {
            var manager = Assemble(NewConfig(), out _);
            yield return Await(manager.InitializeAsync());
            var successes = 0;
            var errors = new List<Exception>();
            var cancellations = 0;
            yield return Run(manager, manager.LoadAsync<string>(ResourceSource.Local, "missing",
                onCompleted: value => { successes++; value.Dispose(); },
                onError: errors.Add, onCancelled: () => cancellations++));
            Assert.That(errors.Count, Is.EqualTo(1));
            Assert.That(((ResourceException)errors[0]).ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.LoaderNotRegistered));

            manager.RegisterLoader(ResourceSource.Local, new ValueLoader("unused"));
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                yield return Run(manager, manager.LoadAsync<string>(ResourceSource.Local, "canceled",
                    onCompleted: value => { successes++; value.Dispose(); },
                    onError: errors.Add, onCancelled: () => cancellations++, token: canceled.Token));
            }
            Assert.That(successes, Is.Zero);
            Assert.That(errors.Count, Is.EqualTo(1));
            Assert.That(cancellations, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CancelLoadAThenLoadBDoesNotDeliverALateSuccessOrOverwriteB()
        {
            var manager = Assemble(NewConfig(), out var system);
            yield return Await(manager.InitializeAsync());
            var loader = new SwitchingLoader();
            manager.RegisterLoader(ResourceSource.Local, loader, new LoaderPolicy(2, null));
            var aSuccesses = 0;
            var aCancellations = 0;
            var bSuccesses = 0;
            var errors = new List<Exception>();
            string displayed = null;
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            using (var cancelA = new CancellationTokenSource())
            {
                Coroutine observingA = null;
                Coroutine observingB = null;
                try
                {
                    observingA = manager.StartCoroutine(manager.LoadAsync<string>(ResourceSource.Local, "A",
                        onCompleted: value => { using (value) displayed = value.Value; aSuccesses++; },
                        onError: errors.Add, onCancelled: () => aCancellations++, token: cancelA.Token));
                    yield return Await(loader.AStarted.Task);
                    cancelA.Cancel();
                    // 不等待 A 后端真正结束便切换到 B，模拟界面快速更换资源。
                    observingB = manager.StartCoroutine(manager.LoadAsync<string>(ResourceSource.Local, "B",
                        onCompleted: value => { using (value) displayed = value.Value; bSuccesses++; },
                        onError: errors.Add));
                    yield return Until(() => aCancellations == 1 && bSuccesses == 1);
                    Assert.That(displayed, Is.EqualTo("B"));
                    Assert.That(aSuccesses, Is.Zero);
                    Assert.That(errors, Is.Empty);

                    loader.CompleteA();
                    yield return Until(() => Volatile.Read(ref loader.AReleases) == 1);
                    yield return Await(system.DisposeAsync().AsTask());
                    yield return null; // 让已经排队的协程回调也有机会执行。
                    Assert.That(displayed, Is.EqualTo("B"));
                    Assert.That(aSuccesses, Is.Zero, "A 的迟到产物必须被释放，不能再交给已取消的界面请求。");
                    Assert.That(aCancellations, Is.EqualTo(1));
                    Assert.That(bSuccesses, Is.EqualTo(1));
                    Assert.That(errors, Is.Empty);
                    Assert.That(loader.AReleaseThread, Is.EqualTo(mainThread));
                }
                finally
                {
                    cancelA.Cancel();
                    loader.CompleteA();
                    if (observingA != null) manager.StopCoroutine(observingA);
                    if (observingB != null) manager.StopCoroutine(observingB);
                }
            }
        }

        [UnityTest]
        public IEnumerator RentCoroutineAndGameObjectShortcutReuseTheReturnedInstance()
        {
            var manager = CreatePrefabManager(out var system, out _);
            yield return Await(manager.InitializeAsync());
            InstanceLease<GameObject> first = null;
            Exception failure = null;
            GameObject instance;
            try
            {
                yield return Run(manager, manager.RentAsync<GameObject>(ResourceSource.Local, "prefab",
                    onCompleted: lease => first = lease, onError: error => failure = error));
                Assert.That(failure, Is.Null);
                Assert.That(first, Is.Not.Null);
                instance = first.Value;
            }
            finally { first?.Dispose(); }
            yield return Until(() => system.Snapshot().Pools.Single().Idle == 1);

            var second = manager.RentAsync<GameObject>(ResourceSource.Local, "prefab", CancellationToken.None);
            yield return Await(second);
            using (var lease = second.Result)
            {
                Assert.That(lease.Value, Is.SameAs(instance));
                Assert.That(lease.Value.activeSelf, Is.True);
            }
            var canceledCount = 0;
            var errorCount = 0;
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                yield return Run(manager, manager.RentAsync<GameObject>(ResourceSource.Local, "canceled",
                    onCompleted: lease => { lease.Dispose(); Assert.Fail("Canceled rent succeeded."); },
                    onError: error => errorCount++, onCancelled: () => canceledCount++, token: canceled.Token));
            }
            Assert.That(canceledCount, Is.EqualTo(1));
            Assert.That(errorCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PreloadCreatesDistinctInstancesHonorsFrameBudgetAndReusesThem()
        {
            var manager = CreatePrefabManager(out var system, out var factory);
            yield return Await(manager.InitializeAsync());
            var itemProgress = new List<double?>();
            var totalProgress = new List<double?>();
            var preloading = manager.PreloadGameObjectAsync(new List<PreloadConfig>
            {
                new PreloadConfig { Source = ResourceSource.Local, Address = "prefab", Count = 5,
                    Progress = new InlineProgress(p => itemProgress.Add(p.Fraction)) }
            }, new InlineProgress(p => totalProgress.Add(p.Fraction)), instancesPerFrame: 2);
            yield return Await(preloading);
            Assert.That(preloading.Result.IsFullyCompleted, Is.True);
            Assert.That(preloading.Result.Total, Is.EqualTo(5));
            Assert.That(preloading.Result.Completed, Is.EqualTo(5));
            yield return Until(() => system.Snapshot().Pools.Single().Idle == 5);
            Assert.That(factory.Created.Distinct().Count(), Is.EqualTo(5));
            Assert.That(factory.Created.All(instance => !instance.activeSelf), Is.True);
            Assert.That(factory.Frames.GroupBy(frame => frame).Max(group => group.Count()), Is.LessThanOrEqualTo(2));
            Assert.That(itemProgress, Is.EqualTo(new double?[] { .2, .4, .6, .8, 1 }));
            Assert.That(totalProgress, Is.EqualTo(itemProgress));

            var leases = new List<InstanceLease<GameObject>>();
            try
            {
                for (var i = 0; i < 5; i++)
                {
                    var renting = manager.RentAsync<GameObject>(ResourceSource.Local, "prefab", CancellationToken.None);
                    yield return Await(renting);
                    leases.Add(renting.Result);
                }
                Assert.That(factory.Created.Count, Is.EqualTo(5), "预热必须保留五个不同实例，而非反复租还一个。");
                Assert.That(leases.Select(lease => lease.Value).Distinct().Count(), Is.EqualTo(5));
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }

        [UnityTest]
        public IEnumerator PreloadReportsPerItemFailureAndCancellationWhileContinuingOtherItems()
        {
            var manager = CreatePrefabManager(out var system, out _);
            yield return Await(manager.InitializeAsync());
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                var failedItem = new PreloadConfig { Source = ResourceSource.Remote, Address = "missing", Count = 1 };
                var canceledItem = new PreloadConfig { Source = ResourceSource.Local, Address = "cancel", Count = 2, Token = canceled.Token };
                var task = manager.PreloadGameObjectAsync(new List<PreloadConfig>
                {
                    failedItem, canceledItem,
                    new PreloadConfig { Source = ResourceSource.Local, Address = "ok", Count = 2 },
                    new PreloadConfig { Source = ResourceSource.Local, Address = "ignored", Count = -1 }
                }, instancesPerFrame: 0);
                yield return Await(task);
                Assert.That(task.Result.Total, Is.EqualTo(5));
                Assert.That(task.Result.Completed, Is.EqualTo(2));
                Assert.That(task.Result.IsFullyCompleted, Is.False);
                Assert.That(task.Result.Canceled, Is.EqualTo(new[] { canceledItem }));
                Assert.That(task.Result.Failed.Count, Is.EqualTo(1));
                Assert.That(task.Result.Failed[0].Key, Is.SameAs(failedItem));
                Assert.That(((ResourceException)task.Result.Failed[0].Value).ResourceError.DiagnosticCode,
                    Is.EqualTo(DiagnosticCodes.LoaderNotRegistered));
                yield return Until(() => system.Snapshot().Pools.Sum(pool => pool.Active) == 0);
                Assert.That(system.Snapshot().Pools.Sum(pool => pool.Idle), Is.EqualTo(2));
            }
        }

        [UnityTest]
        public IEnumerator BatchCancellationReturnsInstancesAlreadyCreatedAndStopsLaterItems()
        {
            var manager = CreatePrefabManager(out var system, out var factory);
            yield return Await(manager.InitializeAsync());
            using (var cancellation = new CancellationTokenSource())
            {
                var task = manager.PreloadGameObjectAsync(new List<PreloadConfig>
                {
                    new PreloadConfig { Source = ResourceSource.Local, Address = "partial", Count = 4 },
                    new PreloadConfig { Source = ResourceSource.Local, Address = "later", Count = 1 }
                }, new InlineProgress(_ => cancellation.Cancel()), cancellation.Token, instancesPerFrame: 1);
                yield return Finish(task);
                Assert.That(task.IsCanceled, Is.True);
                yield return Until(() => system.Snapshot().Pools.Sum(pool => pool.Active) == 0);
                // 取消可在池完成实例化后被观察到；这类迟到实例必须被销毁，不能留下租约。
                yield return Until(() => factory.Created.Count(instance => instance != null) == 1);
                Assert.That(system.Snapshot().Pools.Sum(pool => pool.Idle), Is.EqualTo(1));
                Assert.That(factory.Created[0].activeSelf, Is.False);
                Assert.That(system.Snapshot().Entries.Any(entry => entry.Key.Address == "later"), Is.False);
            }
        }

        [UnityTest]
        public IEnumerator PreloadCoroutineReportsSuccessCancellationAndArgumentFailure()
        {
            var manager = CreatePrefabManager(out _, out _);
            yield return Await(manager.InitializeAsync());
            PreloadResult result = null;
            Exception failure = null;
            var canceledCount = 0;
            yield return Run(manager, manager.PreloadGameObject(new List<PreloadConfig>(),
                onCompleted: value => result = value, onError: error => failure = error));
            Assert.That(result.IsFullyCompleted, Is.True);
            Assert.That(result.Total, Is.Zero);
            Assert.That(failure, Is.Null);

            yield return Run(manager, manager.PreloadGameObject(null, onError: error => failure = error));
            Assert.That(failure, Is.TypeOf<ArgumentNullException>());
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                yield return Run(manager, manager.PreloadGameObject(new List<PreloadConfig>
                {
                    new PreloadConfig { Source = ResourceSource.Local, Address = "never", Count = 1 }
                }, onCancelled: () => canceledCount++, token: cancellation.Token));
            }
            Assert.That(canceledCount, Is.EqualTo(1));
        }

        private ResourceManager CreatePrefabManager(out RS system, out RecordingFactory factory)
        {
            var prefab = TrackObject(new GameObject("ResourceSystemPlayModePrototype"));
            prefab.SetActive(false);
            factory = new RecordingFactory(instance => TrackObject(instance));
            var config = NewConfig();
            config.InstanceFactory = new ExistingFactoryConfig { Factory = factory };
            var manager = Assemble(config, out system);
            // Manager 注册入口要求初始化成功；装配期直接由核心接入测试的原型来源。
            system.RegisterLoader(ResourceSource.Local, new ValueLoader(prefab));
            return manager;
        }

        private static IEnumerator Run(ResourceManager manager, IEnumerator operation)
        {
            var finished = false;
            var running = manager.StartCoroutine(Observe(operation, () => finished = true));
            try { yield return Until(() => finished); }
            finally { if (manager != null) manager.StopCoroutine(running); }
        }

        private static IEnumerator Observe(IEnumerator operation, Action completed)
        {
            yield return operation;
            completed();
        }

        private static void AssertCode(Task task, string code)
        {
            var error = Assert.Throws<ResourceException>(() => task.GetAwaiter().GetResult());
            Assert.That(error.ResourceError.DiagnosticCode, Is.EqualTo(code));
        }

        [Serializable]
        public sealed class CallbackInitializer : InitializerConfig
        {
            [NonSerialized] public Func<CancellationToken, Task> Callback;
            public override Func<CancellationToken, Task> CreateInitializer() => Callback;
        }

        [Serializable]
        public sealed class RejectCreationLoaderConfig : LoaderConfig
        {
            public override void Validate() => throw new InvalidOperationException("Deferred factory must not be validated.");
            public override ILoader CreateLoader() => throw new InvalidOperationException("Deferred factory must not be invoked.");
        }

        [Serializable]
        public sealed class ExistingFactoryConfig : InstanceFactoryConfig
        {
            [NonSerialized] public IInstanceFactory Factory;
            public override IInstanceFactory CreateFactory() => Factory;
        }

        private sealed class ValueLoader : ILoader
        {
            private readonly object _value;
            public ValueLoader(object value) { _value = value; }
            public Task<IResource> LoadAsync(ResourceKey key,
                IProgress<OperationProgress> progress, CancellationToken token)
                => Task.FromResult<IResource>(new Product(_value));
        }

        private sealed class WaitingLoader : ILoader
        {
            public async Task<IResource> LoadAsync(ResourceKey key,
                IProgress<OperationProgress> progress, CancellationToken token)
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Only cancellation can complete this loader.");
            }
        }

        private sealed class SwitchingLoader : ILoader
        {
            public readonly TaskCompletionSource<bool> AStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<IResource> _a = new TaskCompletionSource<IResource>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int AReleases;
            public int AReleaseThread;
            public Task<IResource> LoadAsync(ResourceKey key,
                IProgress<OperationProgress> progress, CancellationToken token)
            {
                if (key.Address != "A") return Task.FromResult<IResource>(new Product(key.Address));
                AStarted.TrySetResult(true);
                return _a.Task; // 故意不立即响应取消，用于验证框架对迟到产物的所有权。
            }
            public void CompleteA() => _a.TrySetResult(new Product("late-A", () =>
            {
                AReleaseThread = Thread.CurrentThread.ManagedThreadId;
                Interlocked.Increment(ref AReleases);
            }));
        }

        private sealed class RecordingFactory : IInstanceFactory
        {
            private readonly GameObjectFactory _inner = new GameObjectFactory();
            private readonly Action<GameObject> _track;
            public readonly List<GameObject> Created = new List<GameObject>();
            public readonly List<int> Frames = new List<int>();
            public RecordingFactory(Action<GameObject> track) { _track = track; }
            public Type PrototypeType => typeof(GameObject);
            public object Create(object prototype)
            {
                var instance = (GameObject)_inner.Create(prototype);
                _track(instance);
                Created.Add(instance);
                Frames.Add(Time.frameCount);
                return instance;
            }
            public void OnRent(object instance) => _inner.OnRent(instance);
            public void OnReturn(object instance) => _inner.OnReturn(instance);
            public void Destroy(object instance) => _inner.Destroy(instance);
        }

        private sealed class InlineProgress : IProgress<OperationProgress>
        {
            private readonly Action<OperationProgress> _report;
            public InlineProgress(Action<OperationProgress> report) { _report = report; }
            public void Report(OperationProgress value) => _report(value);
        }
    }
}
