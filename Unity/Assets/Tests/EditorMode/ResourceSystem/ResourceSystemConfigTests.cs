using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ToolKit.Tools.Common;
using UnityEditor;
using UnityEngine;
using UnityToolKit.Runtime.ResourceSystem;
using Object = UnityEngine.Object;

namespace Tests.ResourceSystemConfigTests
{
    public class ResourceSystemConfigTests
    {
        private ResourceSystemConfig _config;

        [SetUp]
        public void SetUp() => _config = ScriptableObject.CreateInstance<ResourceSystemConfig>();

        [TearDown]
        public void TearDown()
        {
            if (_config != null && !EditorUtility.IsPersistent(_config)) Object.DestroyImmediate(_config);
        }

        [Test]
        public void DefaultConfigProducesIndependentOptions()
        {
            var first = _config.ConvertToOptions();
            var second = _config.ConvertToOptions();
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(first.ExecutionContext, Is.TypeOf<UnityExecutionContext>());
            Assert.That(first.Loaders, Is.Empty);
            Assert.That(first.Converters, Is.Empty);
            Assert.That(first.InstanceFactory, Is.Null);
            Assert.That(first.Initializers, Is.Empty);
        }

        [Test]
        public async Task BuiltInConfigurationLoadsAndConvertsLocalText()
        {
            // 所有磁盘临时数据位于当前工作区的 Temp 内。
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/ResourceSystemConfigTests"));
            Directory.CreateDirectory(directory);
            var filename = Guid.NewGuid().ToString("N") + ".txt";
            var path = Path.Combine(directory, filename);
            try
            {
                File.WriteAllText(path, "配置内容");
                _config.Loaders.Add(new ResourcesLoaderConfig());
                _config.Loaders.Add(new LocalFileLoaderConfig
                {
                    Directory = LocalFileDirectory.Custom, CustomDirectory = directory,
                    MaxConcurrentLoads = 7, TimeoutSeconds = 2.5
                });
                _config.Converters.Add(new TextConverterConfig());
                _config.Converters.Add(new TextureConverterConfig { Readable = true });
                _config.InstanceFactory = new GameObjectFactoryConfig();
                _config.MaxIdlePerPrototype = 0;
                var options = _config.ConvertToOptions();
                var setup = options.Loaders[ResourceSource.Local];
                Assert.That(options.Loaders[ResourceSource.Resources].Loader, Is.TypeOf<ResourcesLoader>());
                Assert.That(setup.Policy.MaxConcurrentLoads, Is.EqualTo(7));
                Assert.That(setup.Policy.LoadTimeout, Is.EqualTo(TimeSpan.FromSeconds(2.5)));
                Assert.That(options.Converters[typeof(Texture2D)], Is.TypeOf<TextureConverter>());
                Assert.That(options.InstanceFactory.PrototypeType, Is.EqualTo(typeof(GameObject)));
                Assert.That(options.Pool.MaxIdlePerPrototype, Is.Zero);
                var key = new ResourceKey(ResourceSource.Local, filename, typeof(string));
                var bytes = await setup.Loader.LoadAsync(key, options.ExecutionContext, null, CancellationToken.None);
                var text = await options.Converters[typeof(string)].ConvertAsync(bytes, key, CancellationToken.None);
                try { Assert.That(text.Value, Is.EqualTo("配置内容")); }
                finally { text.Release(); bytes.Release(); }
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void LaterRegistrationsWinAndOptionsRemainIndependent()
        {
            _config.Loaders.Add(new LocalFileLoaderConfig { MaxConcurrentLoads = 2 });
            var last = new LocalFileLoaderConfig { MaxConcurrentLoads = 9, TimeoutSeconds = 0 };
            _config.Loaders.Add(last);
            _config.Converters.Add(new TextConverterConfig());
            _config.Converters.Add(new CustomConverterConfig());
            var first = _config.ConvertToOptions();
            last.MaxConcurrentLoads = 3;
            var second = _config.ConvertToOptions();
            Assert.That(first.Loaders[ResourceSource.Local].Policy.MaxConcurrentLoads, Is.EqualTo(9));
            Assert.That(first.Loaders[ResourceSource.Local].Policy.LoadTimeout, Is.Null);
            Assert.That(first.Converters[typeof(string)], Is.TypeOf<CustomConverter>());
            Assert.That(second.Loaders[ResourceSource.Local].Policy.MaxConcurrentLoads, Is.EqualTo(3));
            Assert.That(first.Loaders[ResourceSource.Local].Loader, Is.Not.SameAs(second.Loaders[ResourceSource.Local].Loader));
            second.Loaders.Clear();
            Assert.That(first.Loaders.Count, Is.EqualTo(1));
        }

        [TestCase(-1d)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(2147484d)]
        public void InvalidTimeoutIsRejectedBeforeAnyRuntimeObjectIsCreated(double seconds)
        {
            var context = new CustomExecutionContextConfig();
            _config.ExecutionContext = context;
            _config.Loaders.Add(new LocalFileLoaderConfig { TimeoutSeconds = seconds });
            var error = Assert.Throws<InvalidOperationException>(() => _config.ConvertToOptions());
            Assert.That(error.Message, Does.Contain("Loaders[0]"));
            Assert.That(context.Created, Is.Zero);
        }

        [Test]
        public void InvalidPolicyAndMissingSelectionsReportTheirLocation()
        {
            _config.Loaders.Add(new LocalFileLoaderConfig { MaxConcurrentLoads = 0 });
            Assert.That(Assert.Throws<InvalidOperationException>(_config.Validate).Message, Does.Contain("Loaders[0]"));
            _config.Loaders[0] = null;
            Assert.That(Assert.Throws<InvalidOperationException>(_config.Validate).Message, Does.Contain("Loaders[0]"));
            _config.Loaders.Clear();
            _config.ExecutionContext = null;
            Assert.That(Assert.Throws<InvalidOperationException>(_config.Validate).Message, Does.Contain("ExecutionContext"));
            _config.ExecutionContext = new UnityExecutionContextConfig();
            _config.MaxIdlePerPrototype = -1;
            Assert.That(Assert.Throws<InvalidOperationException>(_config.Validate).Message, Does.Contain("MaxIdlePerPrototype"));
        }

        [Test]
        public void FailingCustomFactoryPreservesCauseAndLocation()
        {
            _config.Loaders.Add(new FailingLoaderConfig());
            var error = Assert.Throws<InvalidOperationException>(() => _config.ConvertToOptions());
            Assert.That(error.Message, Does.Contain("Loaders[0]"));
            Assert.That(error.InnerException, Is.TypeOf<NotSupportedException>());
            _config.Loaders[0] = new NullLoaderConfig();
            Assert.That(Assert.Throws<InvalidOperationException>(() => _config.ConvertToOptions()).Message, Does.Contain("null"));
        }

        [Test]
        public async Task CustomFactoriesAreCreatedAtConversionAndInitializerIsDeferred()
        {
            var context = new CustomExecutionContextConfig();
            var initializer = new CustomInitializerConfig { Value = 12 };
            _config.ExecutionContext = context;
            _config.Loaders.Add(new CustomLoaderConfig());
            _config.Converters.Add(new CustomConverterConfig());
            _config.InstanceFactory = new CustomFactoryConfig();
            _config.Initializers.Add(initializer);
            CustomInitializerConfig.ExecutedValue = 0;
            _config.Validate();
            Assert.That(context.Created, Is.Zero);
            var options = _config.ConvertToOptions();
            Assert.That(context.Created, Is.EqualTo(1));
            Assert.That(options.ExecutionContext, Is.SameAs(ImmediateExecutionContext.Instance));
            Assert.That(options.Loaders[ResourceSource.Remote].Loader, Is.TypeOf<LocalFileLoader>());
            Assert.That(options.InstanceFactory, Is.TypeOf<GameObjectFactory>());
            Assert.That(CustomInitializerConfig.ExecutedValue, Is.Zero);
            initializer.Value = 99;
            await options.Initializers[0](CancellationToken.None);
            Assert.That(CustomInitializerConfig.ExecutedValue, Is.EqualTo(12));
        }

        [Test]
        public void DisabledInitializationStoresPolicyWithoutCreatingAnInstance()
        {
            _config.Loaders.Add(new LoaderConfig { InitializeOnStartup = false, Source = ResourceSource.AssetBundle, MaxConcurrentLoads = 3, TimeoutSeconds = 4.5 });
            var options = _config.ConvertToOptions();
            var setup = options.Loaders[ResourceSource.AssetBundle];
            Assert.That(setup.Loader, Is.Null);
            Assert.That(setup.Policy.MaxConcurrentLoads, Is.EqualTo(3));
            Assert.That(setup.Policy.LoadTimeout, Is.EqualTo(TimeSpan.FromSeconds(4.5)));
            Assert.That(options.Initializers, Is.Empty);
        }

        [Test]
        public void InitializationSwitchSkipsFactorySettingsButStillValidatesPolicy()
        {
            var loader = new LocalFileLoaderConfig
            {
                InitializeOnStartup = false,
                Directory = LocalFileDirectory.Custom,
                CustomDirectory = null
            };
            _config.Loaders.Add(loader);
            Assert.That(_config.ConvertToOptions().Loaders[ResourceSource.Local].Loader, Is.Null);
            loader.MaxConcurrentLoads = 0;
            Assert.Throws<InvalidOperationException>(() => _config.ConvertToOptions());
            loader.MaxConcurrentLoads = 2;
            loader.InitializeOnStartup = true;
            Assert.Throws<InvalidOperationException>(() => _config.ConvertToOptions());
            loader.CustomDirectory = ".";
            Assert.That(_config.ConvertToOptions().Loaders[ResourceSource.Local].Loader, Is.TypeOf<LocalFileLoader>());
        }

        [Test]
        public void LegacyConfigurationWithoutTheSwitchStillCreatesItsLoader()
        {
            _config.Loaders.Add(new LocalFileLoaderConfig());
            var json = EditorJsonUtility.ToJson(_config).Replace("\"InitializeOnStartup\":true,", "");
            Assert.That(json, Does.Not.Contain(nameof(LoaderConfig.InitializeOnStartup)));
            var restored = ScriptableObject.CreateInstance<ResourceSystemConfig>();
            try
            {
                EditorJsonUtility.FromJsonOverwrite(json, restored);
                Assert.That(restored.Loaders[0].InitializeOnStartup, Is.True);
                Assert.That(restored.ConvertToOptions().Loaders[ResourceSource.Local].Loader, Is.TypeOf<LocalFileLoader>());
            }
            finally { Object.DestroyImmediate(restored); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void LastLoaderConfigurationReplacesBothInstanceAndPolicy(bool externalLast)
        {
            var external = new LoaderConfig { InitializeOnStartup = false, Source = ResourceSource.AssetBundle, MaxConcurrentLoads = 2, TimeoutSeconds = 0 };
            var local = new LocalFileLoaderConfig { Source = ResourceSource.AssetBundle, MaxConcurrentLoads = 7 };
            _config.Loaders.Add(externalLast ? (LoaderConfig)local : external);
            _config.Loaders.Add(externalLast ? (LoaderConfig)external : local);
            var setup = _config.ConvertToOptions().Loaders[ResourceSource.AssetBundle];
            Assert.That(setup.Loader == null, Is.EqualTo(externalLast));
            Assert.That(setup.Policy.MaxConcurrentLoads, Is.EqualTo(externalLast ? 2 : 7));
        }

        [Test]
        public void AssetRoundTripPreservesBuiltInAndCustomConfiguration()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp")) AssetDatabase.CreateFolder("Assets", "Temp");
            var path = "Assets/Temp/ResourceSystemConfig-" + Guid.NewGuid().ToString("N") + ".asset";
            _config.ExecutionContext = new ImmediateExecutionContextConfig();
            _config.Loaders.Add(new LocalFileLoaderConfig { MaxConcurrentLoads = 8, TimeoutSeconds = 0 });
            _config.Loaders.Add(new LoaderConfig { InitializeOnStartup = false, Source = ResourceSource.AssetBundle, MaxConcurrentLoads = 2, TimeoutSeconds = 60 });
            _config.Converters.Add(new TextureConverterConfig { Readable = true });
            _config.Initializers.Add(new CustomInitializerConfig { Value = 43 });
            try
            {
                AssetDatabase.CreateAsset(_config, path);
                AssetDatabase.SaveAssets();
                Resources.UnloadAsset(_config);
                _config = AssetDatabase.LoadAssetAtPath<ResourceSystemConfig>(path);
                Assert.That(_config.ExecutionContext, Is.TypeOf<ImmediateExecutionContextConfig>());
                Assert.That(_config.Loaders[0].MaxConcurrentLoads, Is.EqualTo(8));
                Assert.That(_config.Loaders[1], Is.TypeOf<LoaderConfig>());
                Assert.That(_config.Loaders[1].InitializeOnStartup, Is.False);
                var external = _config.ConvertToOptions().Loaders[ResourceSource.AssetBundle];
                Assert.That(external.Loader, Is.Null);
                Assert.That(external.Policy.MaxConcurrentLoads, Is.EqualTo(2));
                Assert.That(external.Policy.LoadTimeout, Is.EqualTo(TimeSpan.FromSeconds(60)));
                Assert.That(((TextureConverterConfig)_config.Converters[0]).Readable, Is.True);
                Assert.That(((CustomInitializerConfig)_config.Initializers[0]).Value, Is.EqualTo(43));
                Assert.That(_config.ConvertToOptions().Loaders[ResourceSource.Local].Policy.LoadTimeout, Is.Null);
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        [Test]
        public void InspectorIsRegisteredAndSerializedTypeChangesSupportUndo()
        {
            var editor = UnityEditor.Editor.CreateEditor(_config);
            try { Assert.That(editor, Is.TypeOf<UnityToolKit.Editor.ResourceSystem.ResourceSystemConfigInspector>()); }
            finally { Object.DestroyImmediate(editor); }
            Undo.IncrementCurrentGroup();
            using (var serialized = new SerializedObject(_config))
            {
                serialized.FindProperty(nameof(ResourceSystemConfig.InstanceFactory)).managedReferenceValue = new GameObjectFactoryConfig();
                serialized.ApplyModifiedProperties();
            }
            Assert.That(_config.InstanceFactory, Is.TypeOf<GameObjectFactoryConfig>());
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            Assert.That(_config.InstanceFactory, Is.Null);
            Undo.ClearUndo(_config);
        }
    }

    [Serializable]
    public class CustomExecutionContextConfig : ExecutionContextConfig
    {
        [NonSerialized] public int Created;
        public override IExecutionContext CreateExecutionContext() { Created++; return ImmediateExecutionContext.Instance; }
    }

    [Serializable]
    public class CustomLoaderConfig : LoaderConfig
    {
        public CustomLoaderConfig() { Source = ResourceSource.Remote; }
        public override ILoader CreateLoader() => new LocalFileLoader();
    }

    [Serializable]
    public class FailingLoaderConfig : LoaderConfig
    {
        public override ILoader CreateLoader() => throw new NotSupportedException("测试创建失败");
    }

    [Serializable]
    public class NullLoaderConfig : LoaderConfig
    {
        public override ILoader CreateLoader() => null;
    }

    [Serializable]
    public class CustomConverterConfig : ConverterConfig
    {
        public override Type TargetType => typeof(string);
        public override IProductConverter CreateConverter() => new CustomConverter();
    }

    public class CustomConverter : IProductConverter
    {
        public Task<IResource> ConvertAsync(IResource source, ResourceKey key, CancellationToken cancellation)
            => Task.FromResult<IResource>(new Product("custom"));
    }

    [Serializable]
    public class CustomFactoryConfig : InstanceFactoryConfig
    {
        public override IInstanceFactory CreateFactory() => new GameObjectFactory();
    }

    [Serializable]
    public class CustomInitializerConfig : InitializerConfig
    {
        public int Value;
        public static int ExecutedValue;
        public override Func<CancellationToken, Task> CreateInitializer()
        {
            var value = Value;
            return token => { token.ThrowIfCancellationRequested(); ExecutedValue = value; return Task.CompletedTask; };
        }
    }
}
