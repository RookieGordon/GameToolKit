using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityToolKit.Runtime.ResourceSystem;
using Object = UnityEngine.Object;

namespace Tests.ResourceSystemConfigTests
{
    public class ResourceManagerTests
    {
        private GameObject _host;
        private ResourceManager _manager;
        private ResourceSystemConfig _config;
        private ToolKit.Tools.Common.ResourceSystem _system;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject(nameof(ResourceManagerTests));
            _manager = _host.AddComponent<ResourceManager>();
            _config = ScriptableObject.CreateInstance<ResourceSystemConfig>();
            _config.ExecutionContext = new ImmediateExecutionContextConfig();
        }

        [TearDown]
        public async Task TearDown()
        {
            if (_system != null) await _system.DisposeAsync();
            Object.DestroyImmediate(_config);
            Object.DestroyImmediate(_host);
        }

        private void Assemble()
        {
            _system = new ToolKit.Tools.Common.ResourceSystem(_config.ConvertToOptions());
            // 注入真实核心实例，避免覆盖项目业务使用的 Resources/ResourceSystemConfig.asset。
            typeof(ResourceManager).GetField("_resSystem", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_manager, _system);
        }

        [Test]
        public async Task WaitForInitializationThenRegisterExternalLoader()
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _config.Initializers.Add(new CallbackInitializerConfig { Callback = _ => ready.Task });
            _config.Loaders.Add(new LoaderConfig { InitializeOnStartup = false, Source = ResourceSource.AssetBundle, MaxConcurrentLoads = 2, TimeoutSeconds = 0 });
            Assemble();

            var initialization = _manager.InitializeAsync();
            Assert.That(initialization.IsCompleted, Is.False);
            Assert.That(_manager.IsInitialized, Is.False);
            Assert.That(_manager.InitializeAsync(), Is.SameAs(initialization));
            Assert.Throws<InvalidOperationException>(() => _manager.RegisterAssetBundleLoader(new ValueLoader("bundle")));

            ready.SetResult(true);
            await initialization;
            Assert.That(_manager.IsInitialized, Is.True);
            _manager.RegisterLoader(ResourceSource.Local, new ValueLoader("other-source"));
            using (var other = await _manager.LoadAsync<string>(ResourceSource.Local, "ready", null, CancellationToken.None))
                Assert.That(other.Value, Is.EqualTo("other-source"));
            var missing = Assert.ThrowsAsync<ResourceException>(async () =>
                await _manager.LoadAsync<string>(ResourceSource.AssetBundle, "before-registration", null, CancellationToken.None));
            Assert.That(missing.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.LoaderNotRegistered));

            // 外部系统此时才提供实例；策略沿用配置资产。
            var loader = new ValueLoader("bundle");
            _manager.RegisterAssetBundleLoader(loader);
            using (var asset = await _manager.LoadAsync<string>(ResourceSource.AssetBundle, "after-registration", null, CancellationToken.None))
                Assert.That(asset.Value, Is.EqualTo("bundle"));

            // 再次初始化不能重建系统或丢失后来注册的加载器。
            Assert.That(_manager.InitializeAsync(), Is.SameAs(initialization));
            using (var asset = await _manager.LoadAsync<string>(ResourceSource.AssetBundle, "after-repeat-initialize", null, CancellationToken.None))
                Assert.That(asset.Value, Is.EqualTo("bundle"));
        }

        [Test]
        public async Task FailedInitializationCanBeRetriedBeforeRegistration()
        {
            var attempts = 0;
            var failure = new InvalidOperationException("外部准备工作失败");
            _config.Initializers.Add(new CallbackInitializerConfig
            {
                Callback = _ => ++attempts == 1 ? Task.FromException(failure) : Task.CompletedTask
            });
            Assemble();

            Assert.That(Assert.ThrowsAsync<InvalidOperationException>(() => _manager.InitializeAsync()), Is.SameAs(failure));
            Assert.That(_manager.IsInitialized, Is.False);
            Assert.Throws<InvalidOperationException>(() => _manager.RegisterLoader(ResourceSource.Local, new ValueLoader("local")));

            await _manager.InitializeAsync();
            Assert.That(attempts, Is.EqualTo(2));
            _manager.RegisterLoader(ResourceSource.Local, new ValueLoader("local"));
            using (var asset = await _manager.LoadAsync<string>(ResourceSource.Local, "ready", null, CancellationToken.None))
                Assert.That(asset.Value, Is.EqualTo("local"));
        }

        [Test]
        public async Task LateRegistrationUsesTheSuppliedTimeout()
        {
            Assemble();
            await _manager.InitializeAsync();
            _manager.RegisterLoader(ResourceSource.AssetBundle, new DelayedLoader(),
                new LoaderPolicy(1, TimeSpan.FromMilliseconds(200)));

            var request = _manager.LoadAsync<string>(ResourceSource.AssetBundle, "slow", null, CancellationToken.None);
            Assert.That(await Task.WhenAny(request, Task.Delay(3000)), Is.SameAs(request));
            var error = Assert.ThrowsAsync<ResourceException>(() => request);
            Assert.That(error.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.RequestTimeout));
        }

        [Test]
        public async Task LateRegistrationUsesConfiguredTimeoutSnapshot()
        {
            var configured = new LoaderConfig { InitializeOnStartup = false, Source = ResourceSource.AssetBundle, MaxConcurrentLoads = 1, TimeoutSeconds = 0.2 };
            _config.Loaders.Add(configured);
            Assemble();
            await _manager.InitializeAsync();
            configured.TimeoutSeconds = 0; // 资产改动不能改变运行系统的策略快照。
            _manager.RegisterAssetBundleLoader(new DelayedLoader());

            var request = _manager.LoadAsync<string>(ResourceSource.AssetBundle, "configured-timeout", null, CancellationToken.None);
            Assert.That(await Task.WhenAny(request, Task.Delay(3000)), Is.SameAs(request));
            var error = Assert.ThrowsAsync<ResourceException>(() => request);
            Assert.That(error.ResourceError.DiagnosticCode, Is.EqualTo(DiagnosticCodes.RequestTimeout));
        }

        [Test]
        public async Task ConfiguredConcurrencyAppliesAndExplicitOverrideDoesNotReplaceTheSnapshot()
        {
            var configured = new LoaderConfig { InitializeOnStartup = false, Source = ResourceSource.AssetBundle, MaxConcurrentLoads = 1, TimeoutSeconds = 0 };
            _config.Loaders.Add(configured);
            Assemble();
            await _manager.InitializeAsync();
            configured.MaxConcurrentLoads = 9;

            var first = new CountingDelayedLoader();
            _manager.RegisterAssetBundleLoader(first);
            await CheckConcurrentLoads(first, "configured", 1);

            var overridden = new CountingDelayedLoader();
            _manager.RegisterAssetBundleLoader(overridden, new LoaderPolicy(2, null));
            await CheckConcurrentLoads(overridden, "overridden", 2);

            var restored = new CountingDelayedLoader();
            _manager.RegisterAssetBundleLoader(restored);
            await CheckConcurrentLoads(restored, "restored", 1);
        }

        private async Task CheckConcurrentLoads(CountingDelayedLoader loader, string address, int expected)
        {
            var first = _manager.LoadAsync<string>(ResourceSource.AssetBundle, address + "/a", null, CancellationToken.None);
            var second = _manager.LoadAsync<string>(ResourceSource.AssetBundle, address + "/b", null, CancellationToken.None);
            try
            {
                var started = expected == 1 ? loader.FirstStarted.Task : loader.SecondStarted.Task;
                Assert.That(await Task.WhenAny(started, Task.Delay(3000)), Is.SameAs(started));
                if (expected == 1)
                    Assert.That(await Task.WhenAny(loader.SecondStarted.Task, Task.Delay(100)), Is.Not.SameAs(loader.SecondStarted.Task));
                Assert.That(Volatile.Read(ref loader.Calls), Is.EqualTo(expected));
            }
            finally
            {
                loader.Ready.TrySetResult(true);
                var assets = await Task.WhenAll(first, second);
                foreach (var asset in assets) asset.Dispose();
            }
        }

        [Test]
        public async Task ReplacingExternalLoaderPreservesInFlightAndCachedResults()
        {
            Assemble();
            await _manager.InitializeAsync();
            var old = new DelayedLoader();
            _manager.RegisterLoader(ResourceSource.AssetBundle, old, new LoaderPolicy(1, null));
            var inFlight = _manager.LoadAsync<string>(ResourceSource.AssetBundle, "old", null, CancellationToken.None);
            await old.Started.Task;

            _manager.RegisterLoader(ResourceSource.AssetBundle, new ValueLoader("new"));
            using (var asset = await _manager.LoadAsync<string>(ResourceSource.AssetBundle, "new", null, CancellationToken.None))
                Assert.That(asset.Value, Is.EqualTo("new"));
            Assert.That(old.Token.IsCancellationRequested, Is.False);
            old.Completion.SetResult(new Product("old"));
            using (var asset = await inFlight) Assert.That(asset.Value, Is.EqualTo("old"));
            using (var cached = await _manager.LoadAsync<string>(ResourceSource.AssetBundle, "old", null, CancellationToken.None))
                Assert.That(cached.Value, Is.EqualTo("old"));
        }

        [Serializable]
        public class CallbackInitializerConfig : InitializerConfig
        {
            [NonSerialized] public Func<CancellationToken, Task> Callback;
            public override Func<CancellationToken, Task> CreateInitializer() => Callback;
        }

        private class ValueLoader : ILoader
        {
            private readonly string _value;
            public ValueLoader(string value) { _value = value; }
            public Task<IResource> LoadAsync(ResourceKey key, IExecutionContext context, IProgress<OperationProgress> progress, CancellationToken cancellation)
                => Task.FromResult<IResource>(new Product(_value));
        }

        private class DelayedLoader : ILoader
        {
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<IResource> Completion = new TaskCompletionSource<IResource>(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationToken Token;

            public async Task<IResource> LoadAsync(ResourceKey key, IExecutionContext context, IProgress<OperationProgress> progress, CancellationToken cancellation)
            {
                Token = cancellation;
                using (cancellation.Register(() => Completion.TrySetCanceled()))
                {
                    Started.TrySetResult(true);
                    return await Completion.Task;
                }
            }
        }

        private class CountingDelayedLoader : ILoader
        {
            public readonly TaskCompletionSource<bool> FirstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> SecondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Calls;

            public async Task<IResource> LoadAsync(ResourceKey key, IExecutionContext context, IProgress<OperationProgress> progress, CancellationToken cancellation)
            {
                if (Interlocked.Increment(ref Calls) == 1) FirstStarted.TrySetResult(true);
                else SecondStarted.TrySetResult(true);
                using (cancellation.Register(() => Ready.TrySetCanceled())) await Ready.Task;
                return new Product(key.Address);
            }
        }
    }
}
