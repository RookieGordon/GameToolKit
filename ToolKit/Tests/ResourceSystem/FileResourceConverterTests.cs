using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class FileResourceConverterTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gt-convert-" + Guid.NewGuid().ToString("N"));

        public FileResourceConverterTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            Directory.Delete(_root, true);
        }

        [Fact]
        public async Task LocalAndRemoteLoaders_ReuseOneConversionTable_ForBytesAndText()
        {
            var path = Path.Combine(_root, "source.txt");
            await File.WriteAllTextAsync(path, "shared file", new UTF8Encoding(false));
            var cache = NewCache();
            var downloads = 0;
            var converters = FileResourceConverters.CreateDefault();
            using var manager = new ResourceManager(ImmediateExecutionContext.Instance, fileCache: cache);
            manager.RegisterLoader(ResourceSource.Local, new LocalFileLoader(converters));
            manager.RegisterLoader(ResourceSource.Remote, new RemoteFileLoader(cache, (_, destination, _) =>
            {
                Interlocked.Increment(ref downloads);
                File.Copy(path, destination);
                return Task.CompletedTask;
            }, converters));

            try
            {
                await manager.InitializeAsync();
                using var localBytes = await manager.LoadAsync<byte[]>(path, ResourceSource.Local);
                using var localText = await manager.LoadAsync<string>(path, ResourceSource.Local);
                using var remoteBytes = await manager.LoadAsync<byte[]>("https://example.test/file", ResourceSource.Remote);
                using var remoteText = await manager.LoadAsync<string>("https://example.test/file", ResourceSource.Remote);

                Assert.Equal("shared file", Encoding.UTF8.GetString(localBytes.Value));
                Assert.Equal("shared file", localText.Value);
                Assert.Equal(localBytes.Value, remoteBytes.Value);
                Assert.Equal(localText.Value, remoteText.Value);
                Assert.Equal(1, downloads);
                Assert.Equal(4, manager.GetSnapshot().ResourceRows.Count);
            }
            finally
            {
                manager.Dispose();
                await cache.DisposeAsync();
            }
            Assert.True(File.Exists(path)); // 资源释放不取得用户源文件的删除权。
        }

        [Theory]
        [InlineData(ResourceSource.Local)]
        [InlineData(ResourceSource.Remote)]
        public async Task OneCustomConverter_SupportsMultipleTypes_AndIsolatesConversionKeys(ResourceSource source)
        {
            var converter = new RecordingConverter();
            var converters = new FileResourceConverters();
            converters.Register(typeof(FakeAsset), converter);
            converters.Register(typeof(FakePrefab), converter);
            var cache = NewCache();
            var downloads = 0;
            IResourceLoader loader = source == ResourceSource.Local
                ? new LocalFileLoader(converters)
                : new RemoteFileLoader(cache, (_, path, _) =>
                {
                    Interlocked.Increment(ref downloads);
                    File.WriteAllText(path, "content");
                    return Task.CompletedTask;
                }, converters);
            using var manager = new ResourceManager(ImmediateExecutionContext.Instance, fileCache: cache);
            manager.RegisterLoader(source, loader);
            var address = source == ResourceSource.Local ? Path.Combine(_root, "source") : "https://example.test/file";
            try
            {
                await manager.InitializeAsync();
                using var first = await manager.LoadAsync<FakeAsset>(address, source, Options("small"));
                using var same = await manager.LoadAsync<FakeAsset>(address, source, Options("small"));
                using var otherKey = await manager.LoadAsync<FakeAsset>(address, source, Options("large"));
                using var otherType = await manager.LoadAsync<FakePrefab>(address, source, Options("small"));
                using var defaultValue = await manager.LoadAsync<FakeAsset>(address, source);
                using var nullKey = await manager.LoadAsync<FakeAsset>(address, source, Options(null));
                using var emptyKey = await manager.LoadAsync<FakeAsset>(address, source, Options(""));

                Assert.Same(first.Value, same.Value);
                Assert.NotSame(first.Value, otherKey.Value);
                Assert.Equal("small", first.Value.Name);
                Assert.Equal("large", otherKey.Value.Name);
                Assert.Equal("small", otherType.Value.Name);
                Assert.Same(defaultValue.Value, nullKey.Value);
                Assert.Same(defaultValue.Value, emptyKey.Value);
                Assert.NotSame(first.Value, defaultValue.Value);
                Assert.Equal(new string?[] { "small", "large", "small", null }, converter.Keys);
                Assert.Equal(source == ResourceSource.Remote ? 1 : 0, downloads);

                manager.Dispose();
                await V2Test.WaitUntilAsync(() => converter.ReleaseCount == 4);
                Assert.False(first.IsValid);
            }
            finally
            {
                manager.Dispose();
                await cache.DisposeAsync();
            }
            Assert.Equal(4, converter.ReleaseCount);
        }

        [Fact]
        public void DuplicateTargetType_IsRejected_WithoutReplacingItsConverter()
        {
            var converters = new FileResourceConverters();
            var original = new RecordingConverter();
            converters.Register(typeof(FakeAsset), original);

            Assert.Throws<InvalidOperationException>(() =>
                converters.Register(typeof(FakeAsset), new RecordingConverter()));
            Assert.Same(original, converters.Get(typeof(FakeAsset)));
        }

        [Theory]
        [InlineData(ResourceSource.Local)]
        [InlineData(ResourceSource.Remote)]
        public async Task SameTypeNameFromDifferentAssemblies_HasSeparateResourceIdentity(ResourceSource source)
        {
            var firstType = CreateResourceType();
            var secondType = CreateResourceType();
            Assert.Equal(firstType.FullName, secondType.FullName);
            Assert.NotEqual(firstType.Assembly, secondType.Assembly);
            var converters = new FileResourceConverters();
            var converter = new ConstructedTypeConverter();
            converters.Register(firstType, converter);
            converters.Register(secondType, converter);
            var cache = NewCache();
            IResourceLoader loader = source == ResourceSource.Local
                ? new LocalFileLoader(converters)
                : new RemoteFileLoader(cache, (_, path, _) =>
                {
                    File.WriteAllText(path, "content");
                    return Task.CompletedTask;
                }, converters);
            var address = source == ResourceSource.Local ? Path.Combine(_root, "source") : "https://example.test/file";
            try
            {
                await cache.InitializeAsync();
                var first = await loader.ResolveAsync(new ResourceRequest(source, address, firstType, null), default);
                var second = await loader.ResolveAsync(new ResourceRequest(source, address, secondType, null), default);

                Assert.NotEqual(first.LocalKey, second.LocalKey);
                var firstAsset = await loader.LoadAsync(first, null!, default);
                var secondAsset = await loader.LoadAsync(second, null!, default);
                try
                {
                    Assert.Equal(firstType, firstAsset.Value.GetType());
                    Assert.Equal(secondType, secondAsset.Value.GetType());
                }
                finally
                {
                    await firstAsset.ReleaseAsync();
                    await secondAsset.ReleaseAsync();
                }
            }
            finally
            {
                await cache.DisposeAsync();
            }
        }

        [Theory]
        [InlineData(ResourceSource.Local, false)]
        [InlineData(ResourceSource.Local, true)]
        [InlineData(ResourceSource.Remote, false)]
        [InlineData(ResourceSource.Remote, true)]
        public async Task InvalidConversionRequest_FailsBeforeReadingOrDownloading(ResourceSource source, bool oldStringParameter)
        {
            var cache = NewCache();
            var downloads = 0;
            var converters = FileResourceConverters.CreateDefault();
            IResourceLoader loader = source == ResourceSource.Local
                ? new LocalFileLoader(converters)
                : new RemoteFileLoader(cache, (_, _, _) =>
                {
                    downloads++;
                    throw new InvalidOperationException("Invalid requests must not download");
                }, converters);
            var request = new ResourceRequest(source,
                source == ResourceSource.Local ? Path.Combine(_root, "missing") : "https://example.test/file",
                oldStringParameter ? typeof(string) : typeof(FakeAsset),
                oldStringParameter ? "text" : null);
            try
            {
                var failure = await Assert.ThrowsAsync<ResourceLoadException>(() => loader.ResolveAsync(request, default));
                Assert.Equal(oldStringParameter ? DiagnosticCodes.LoaderResolveFailed : DiagnosticCodes.AssetUnsupportedRepresentation,
                    failure.Error.DiagnosticCode);
                Assert.Equal(oldStringParameter ? LoadStage.Resolve : LoadStage.Convert, failure.Error.Stage);
                Assert.Equal(CleanupStatus.Complete, failure.Error.Cleanup);
                Assert.Equal(0, downloads);
            }
            finally
            {
                await cache.DisposeAsync();
            }
        }

        [Theory]
        [InlineData(typeof(byte[]))]
        [InlineData(typeof(string))]
        public async Task BuiltInConversion_PreCancelledToken_RemainsCancellation(Type targetType)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var converter = FileResourceConverters.CreateDefault().Get(targetType);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                converter.ConvertAsync(Path.Combine(_root, "missing"), targetType, null, cancellation.Token));
        }

        [Theory]
        [InlineData(ResourceSource.Local)]
        [InlineData(ResourceSource.Remote)]
        public async Task CustomConversionFailure_PreservesCause_WithoutDeletingTheFile(ResourceSource source)
        {
            var converters = new FileResourceConverters();
            var cause = new InvalidOperationException("Conversion failed");
            var converter = new FailingConverter(cause);
            converters.Register(typeof(FakeAsset), converter);
            var cache = NewCache();
            var path = Path.Combine(_root, "source");
            File.WriteAllText(path, "content");
            IResourceLoader loader = source == ResourceSource.Local
                ? new LocalFileLoader(converters)
                : new RemoteFileLoader(cache, (_, destination, _) =>
                {
                    File.Copy(path, destination);
                    return Task.CompletedTask;
                }, converters);
            try
            {
                await cache.InitializeAsync();
                var resolved = await loader.ResolveAsync(new ResourceRequest(source,
                    source == ResourceSource.Local ? path : "https://example.test/file", typeof(FakeAsset), null), default);
                var failure = await Assert.ThrowsAsync<ResourceLoadException>(() => loader.LoadAsync(resolved, null!, default));

                Assert.Equal(DiagnosticCodes.AssetConversionFailed, failure.Error.DiagnosticCode);
                Assert.Equal("asset.decode_failed", failure.Error.DiagnosticCode); // 保留既有日志诊断码。
                Assert.Equal(LoadStage.Convert, failure.Error.Stage);
                Assert.Equal(CleanupStatus.Unknown, failure.Error.Cleanup);
                Assert.Same(cause, failure.Error.Cause);
                Assert.True(File.Exists(converter.Path));
            }
            finally
            {
                await cache.DisposeAsync();
            }
        }

        private FileCache NewCache() => new FileCache(new FileCacheOptions { Directory = Path.Combine(_root, "cache") });

        private static RequestOptions Options(string? key) => new RequestOptions { Parameters = new FileLoadParameters(key) };

        private static Type CreateResourceType()
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("FileConversionTest_" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule("Resources").DefineType("TestResources.SameName", TypeAttributes.Public);
            type.DefineDefaultConstructor(MethodAttributes.Public);
            return type.CreateTypeInfo()!.AsType();
        }

        private sealed class ConstructedTypeConverter : IFileResourceConverter
        {
            public Task<LoadedAsset> ConvertAsync(string path, Type resultType, string? conversionKey, CancellationToken ct)
            {
                return Task.FromResult(LoadedAsset.FromUnmanaged(Activator.CreateInstance(resultType)!));
            }
        }

        private sealed class RecordingConverter : IFileResourceConverter
        {
            public readonly List<string?> Keys = new List<string?>();
            public int ReleaseCount;

            public Task<LoadedAsset> ConvertAsync(string path, Type resultType, string? conversionKey, CancellationToken ct)
            {
                Keys.Add(conversionKey);
                object value = resultType == typeof(FakeAsset)
                    ? new FakeAsset(conversionKey ?? "default")
                    : new FakePrefab(conversionKey ?? "default");
                return Task.FromResult(new LoadedAsset(value, null, null, () =>
                {
                    Interlocked.Increment(ref ReleaseCount);
                    return Task.CompletedTask;
                }));
            }
        }

        private sealed class FailingConverter : IFileResourceConverter
        {
            private readonly Exception _cause;
            public string? Path;

            public FailingConverter(Exception cause) { _cause = cause; }

            public Task<LoadedAsset> ConvertAsync(string path, Type resultType, string? conversionKey, CancellationToken ct)
            {
                Path = path;
                throw _cause;
            }
        }
    }
}
