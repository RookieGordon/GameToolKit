using System;
using NUnit.Framework;
using ToolKit.Tools.Common;
using ToolKit.Tools.Network;
using UnityEditor;
using UnityEngine;
using UnityToolKit.Runtime.ResourceSystem;
using Object = UnityEngine.Object;

namespace Tests.ResourceSystemConfigTests
{
    public class RemoteFileLoaderConfigTests
    {
        private ResourceSystemConfig _asset;

        [SetUp]
        public void SetUp() => _asset = ScriptableObject.CreateInstance<ResourceSystemConfig>();

        [TearDown]
        public void TearDown()
        {
            if (_asset != null && !EditorUtility.IsPersistent(_asset)) Object.DestroyImmediate(_asset);
        }

        [Test]
        public void DefaultRemoteConfigurationHasExplicitCacheAndDownloadLimits()
        {
            var remote = new RemoteFileLoaderConfig();
            Assert.That(remote.Source, Is.EqualTo(ResourceSource.Remote));
            Assert.That(remote.InitializeOnStartup, Is.True);
            Assert.That(remote.CacheDirectory, Is.EqualTo("ResourceCache"));
            Assert.That(remote.StartupTargetBytes, Is.EqualTo(512L * 1024 * 1024));
            Assert.That(remote.MaxAgeSeconds, Is.Zero);
            Assert.That(remote.MaxConcurrentDownloads, Is.EqualTo(4));
            Assert.That(remote.MaxRetries, Is.EqualTo(DownloadOptions.DefaultMaxRetries));
            Assert.That(remote.RetryDelayMs, Is.EqualTo(DownloadOptions.DefaultRetryDelayMs));
            Assert.That(remote.MaxRetryDelayMs, Is.EqualTo(DownloadOptions.DefaultMaxRetryDelayMs));
            Assert.That(remote.BufferSize, Is.EqualTo(DownloadOptions.DefaultBufferSize));
            Assert.That(remote.ConnectTimeoutMs, Is.EqualTo(DownloadOptions.DefaultConnectTimeoutMs));
            Assert.That(remote.ReadTimeoutMs, Is.EqualTo(DownloadOptions.DefaultReadTimeoutMs));
            Assert.That(remote.MaxBytesPerSecond, Is.Zero);
            Assert.DoesNotThrow(remote.Validate);
        }

        [Test]
        public void AllDownloadOptionsAreSerializedAndEditableWhileDeferred()
        {
            // 使用序列化字段名约束 Inspector 数据，字段尚未实现时仍能编译并给出具体失败。
            var names = new[] { "MaxRetries", "RetryDelayMs", "MaxRetryDelayMs", "BufferSize",
                "ConnectTimeoutMs", "ReadTimeoutMs", "MaxBytesPerSecond" };
            var values = new long[] { 1, 25, 40, 4096, 2500, 3500, 4294967296L };
            _asset.Loaders.Add(new RemoteFileLoaderConfig { InitializeOnStartup = false });
            using (var serialized = new SerializedObject(_asset))
            {
                var entry = serialized.FindProperty(nameof(ResourceSystemConfig.Loaders)).GetArrayElementAtIndex(0);
                for (var i = 0; i < names.Length; i++)
                {
                    var property = entry.FindPropertyRelative(names[i]);
                    Assert.That(property, Is.Not.Null, names[i] + " 必须作为远程加载器配置的可序列化字段公开。");
                    property.longValue = values[i];
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            var restored = ScriptableObject.CreateInstance<ResourceSystemConfig>();
            try
            {
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(_asset), restored);
                Assert.That(restored.Loaders[0].InitializeOnStartup, Is.False);
                using (var serialized = new SerializedObject(restored))
                {
                    var entry = serialized.FindProperty(nameof(ResourceSystemConfig.Loaders)).GetArrayElementAtIndex(0);
                    for (var i = 0; i < names.Length; i++)
                        Assert.That(entry.FindPropertyRelative(names[i]).longValue, Is.EqualTo(values[i]), names[i]);
                }
            }
            finally { Object.DestroyImmediate(restored); }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void EnabledRemoteRejectsMissingCacheDirectory(string directory)
            => AssertInvalid(new RemoteFileLoaderConfig { CacheDirectory = directory });

        [TestCase(0L)]
        [TestCase(-1L)]
        public void EnabledRemoteRequiresPositiveStartupCapacity(long bytes)
            => AssertInvalid(new RemoteFileLoaderConfig { StartupTargetBytes = bytes });

        [TestCase(-1d)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.MaxValue)]
        public void EnabledRemoteRejectsInvalidFileLifetime(double seconds)
            => AssertInvalid(new RemoteFileLoaderConfig { MaxAgeSeconds = seconds });

        [TestCase(0)]
        [TestCase(-1)]
        public void EnabledRemoteRequiresPositiveDownloadConcurrency(int count)
            => AssertInvalid(new RemoteFileLoaderConfig { MaxConcurrentDownloads = count });

        [TestCase(nameof(RemoteFileLoaderConfig.MaxRetries), -1L)]
        [TestCase(nameof(RemoteFileLoaderConfig.RetryDelayMs), -1L)]
        [TestCase(nameof(RemoteFileLoaderConfig.MaxRetryDelayMs), -1L)]
        [TestCase(nameof(RemoteFileLoaderConfig.BufferSize), (long)DownloadOptions.MinBufferSize - 1)]
        [TestCase(nameof(RemoteFileLoaderConfig.ConnectTimeoutMs), (long)DownloadOptions.MinTimeoutMs - 1)]
        [TestCase(nameof(RemoteFileLoaderConfig.ReadTimeoutMs), (long)DownloadOptions.MinTimeoutMs - 1)]
        [TestCase(nameof(RemoteFileLoaderConfig.MaxBytesPerSecond), -1L)]
        public void InvalidDownloadOptionIsRejectedAndDeferredCreationStillValidatesIt(string name, long invalid)
        {
            var remote = new RemoteFileLoaderConfig();
            var field = typeof(RemoteFileLoaderConfig).GetField(name);
            field.SetValue(remote, field.FieldType == typeof(long) ? (object)invalid : (int)invalid);
            _asset.Loaders.Add(remote);
            var failure = Assert.Throws<InvalidOperationException>(() => _asset.ConvertToOptions());
            Assert.That(failure.Message, Does.Contain("Loaders[0]"));

            remote.InitializeOnStartup = false;
            var deferred = _asset.ConvertToOptions();
            Assert.That(deferred.Initializers, Is.Empty);
            Assert.That(deferred.ShutdownActions, Is.Empty);
            Assert.Throws<InvalidOperationException>(remote.Validate);
            Assert.Throws<InvalidOperationException>(() => remote.CreateDownloadOptions());
        }

        [Test]
        public void DownloadOptionBoundariesAreAcceptedWithoutImplicitClamping()
        {
            var remote = new RemoteFileLoaderConfig
            {
                MaxRetries = 0, RetryDelayMs = 0, MaxRetryDelayMs = 0,
                BufferSize = DownloadOptions.MinBufferSize,
                ConnectTimeoutMs = DownloadOptions.MinTimeoutMs,
                ReadTimeoutMs = DownloadOptions.MinTimeoutMs,
                MaxBytesPerSecond = 0
            };
            var options = remote.CreateDownloadOptions();
            Assert.That(options.MaxRetries, Is.Zero);
            Assert.That(options.RetryDelayMs, Is.Zero);
            Assert.That(options.MaxRetryDelayMs, Is.Zero);
            Assert.That(options.BufferSize, Is.EqualTo(DownloadOptions.MinBufferSize));
            Assert.That(options.ConnectTimeoutMs, Is.EqualTo(DownloadOptions.MinTimeoutMs));
            Assert.That(options.ReadTimeoutMs, Is.EqualTo(DownloadOptions.MinTimeoutMs));
            Assert.That(options.MaxBytesPerSecond, Is.Zero);
        }

        [Test]
        public void CreateDownloadOptionsCopiesEveryFieldIntoAnIndependentSnapshot()
        {
            var remote = new RemoteFileLoaderConfig
            {
                MaxRetries = 2, RetryDelayMs = 120, MaxRetryDelayMs = 650,
                BufferSize = 4096, ConnectTimeoutMs = 2300, ReadTimeoutMs = 3400,
                MaxBytesPerSecond = 4294967296L
            };
            var snapshot = remote.CreateDownloadOptions();
            remote.MaxRetries = 6;
            remote.RetryDelayMs = 0;
            remote.MaxRetryDelayMs = 0;
            remote.BufferSize = 8192;
            remote.ConnectTimeoutMs = 1000;
            remote.ReadTimeoutMs = 1000;
            remote.MaxBytesPerSecond = 0;
            Assert.That(snapshot.MaxRetries, Is.EqualTo(2));
            Assert.That(snapshot.RetryDelayMs, Is.EqualTo(120));
            Assert.That(snapshot.MaxRetryDelayMs, Is.EqualTo(650));
            Assert.That(snapshot.BufferSize, Is.EqualTo(4096));
            Assert.That(snapshot.ConnectTimeoutMs, Is.EqualTo(2300));
            Assert.That(snapshot.ReadTimeoutMs, Is.EqualTo(3400));
            Assert.That(snapshot.MaxBytesPerSecond, Is.EqualTo(4294967296L));
            var next = remote.CreateDownloadOptions();
            snapshot.MaxRetries = 99;
            Assert.That(next, Is.Not.SameAs(snapshot));
            Assert.That(next.MaxRetries, Is.EqualTo(6));
            Assert.That(remote.MaxRetries, Is.EqualTo(6));
        }

        [Test]
        public void DeferredConfigurationKeepsImplementationFieldsWithoutCreatingServices()
        {
            var remote = new RemoteFileLoaderConfig
            {
                InitializeOnStartup = false,
                CacheDirectory = "",
                StartupTargetBytes = 0,
                MaxAgeSeconds = double.NaN,
                MaxConcurrentDownloads = 0,
                MaxConcurrentLoads = 2,
                TimeoutSeconds = 3.5
            };
            _asset.Loaders.Add(remote);
            var options = _asset.ConvertToOptions();
            Assert.That(options.Loaders[ResourceSource.Remote].Loader, Is.Null);
            Assert.That(options.Loaders[ResourceSource.Remote].Policy.MaxConcurrentLoads, Is.EqualTo(2));
            Assert.That(options.Loaders[ResourceSource.Remote].Policy.LoadTimeout, Is.EqualTo(TimeSpan.FromSeconds(3.5)));
            Assert.That(options.Initializers, Is.Empty);
            Assert.That(options.ShutdownActions, Is.Empty);
            Assert.That(remote.CacheDirectory, Is.Empty);
            Assert.That(double.IsNaN(remote.MaxAgeSeconds), Is.True);
            Assert.Throws<InvalidOperationException>(remote.Validate);
        }

        [Test]
        public void DeferredConfigurationStillValidatesItsResourceLoadingPolicy()
        {
            AssertInvalid(new RemoteFileLoaderConfig
            {
                InitializeOnStartup = false, CacheDirectory = "", MaxConcurrentLoads = 0
            });
        }

        [Test]
        public void RemoteConfigurationSurvivesAssetSaveReloadAndSerializedEditsWhileDeferred()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp")) AssetDatabase.CreateFolder("Assets", "Temp");
            var folderName = "RemoteFileConfig-" + Guid.NewGuid().ToString("N");
            var folder = "Assets/Temp/" + folderName;
            AssetDatabase.CreateFolder("Assets/Temp", folderName);
            var path = folder + "/Remote.asset";
            try
            {
                _asset.Loaders.Add(new RemoteFileLoaderConfig
                {
                    InitializeOnStartup = false,
                    CacheDirectory = "Relative/CacheDirectory",
                    StartupTargetBytes = 9876543210L,
                    MaxAgeSeconds = 7200.5,
                    MaxConcurrentDownloads = 7,
                    MaxConcurrentLoads = 3,
                    TimeoutSeconds = 12.25,
                    MaxRetries = 2, RetryDelayMs = 75, MaxRetryDelayMs = 250,
                    BufferSize = 4096, ConnectTimeoutMs = 1800, ReadTimeoutMs = 2900,
                    MaxBytesPerSecond = 4294967296L
                });
                AssetDatabase.CreateAsset(_asset, path);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                _asset = AssetDatabase.LoadAssetAtPath<ResourceSystemConfig>(path);
                Assert.That(_asset.Loaders[0], Is.TypeOf<RemoteFileLoaderConfig>());
                var remote = (RemoteFileLoaderConfig)_asset.Loaders[0];
                Assert.That(remote.Source, Is.EqualTo(ResourceSource.Remote));
                Assert.That(remote.InitializeOnStartup, Is.False);
                Assert.That(remote.CacheDirectory, Is.EqualTo("Relative/CacheDirectory"));
                Assert.That(remote.StartupTargetBytes, Is.EqualTo(9876543210L));
                Assert.That(remote.MaxAgeSeconds, Is.EqualTo(7200.5));
                Assert.That(remote.MaxConcurrentDownloads, Is.EqualTo(7));
                Assert.That(remote.MaxConcurrentLoads, Is.EqualTo(3));
                Assert.That(remote.TimeoutSeconds, Is.EqualTo(12.25));
                Assert.That(remote.MaxRetries, Is.EqualTo(2));
                Assert.That(remote.RetryDelayMs, Is.EqualTo(75));
                Assert.That(remote.MaxRetryDelayMs, Is.EqualTo(250));
                Assert.That(remote.BufferSize, Is.EqualTo(4096));
                Assert.That(remote.ConnectTimeoutMs, Is.EqualTo(1800));
                Assert.That(remote.ReadTimeoutMs, Is.EqualTo(2900));
                Assert.That(remote.MaxBytesPerSecond, Is.EqualTo(4294967296L));

                using (var serialized = new SerializedObject(_asset))
                {
                    var entry = serialized.FindProperty(nameof(ResourceSystemConfig.Loaders)).GetArrayElementAtIndex(0);
                    entry.FindPropertyRelative(nameof(RemoteFileLoaderConfig.CacheDirectory)).stringValue = "Edited/DeferredCache";
                    entry.FindPropertyRelative(nameof(RemoteFileLoaderConfig.StartupTargetBytes)).longValue = 321;
                    entry.FindPropertyRelative(nameof(RemoteFileLoaderConfig.MaxAgeSeconds)).doubleValue = 90;
                    entry.FindPropertyRelative(nameof(RemoteFileLoaderConfig.MaxConcurrentDownloads)).intValue = 2;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                remote = (RemoteFileLoaderConfig)_asset.Loaders[0];
                Assert.That(remote.InitializeOnStartup, Is.False);
                Assert.That(remote.CacheDirectory, Is.EqualTo("Edited/DeferredCache"));
                Assert.That(remote.StartupTargetBytes, Is.EqualTo(321));
                Assert.That(remote.MaxAgeSeconds, Is.EqualTo(90));
                Assert.That(remote.MaxConcurrentDownloads, Is.EqualTo(2));
                Assert.That(_asset.ConvertToOptions().Initializers, Is.Empty);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        private void AssertInvalid(RemoteFileLoaderConfig remote)
        {
            _asset.Loaders.Add(remote);
            var error = Assert.Throws<InvalidOperationException>(() => _asset.ConvertToOptions());
            Assert.That(error.Message, Does.Contain("Loaders[0]"));
        }
    }
}
