/*
 * 文件加载器端到端用例：真实 LocalFileLoader + 转换表，验证转换差异参数进入身份、
 * 相同身份命中复用、不同身份不错误合并。这是复核清单第 4 项的最小验收。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    [Collection("ResourceSystem")]
    public sealed class FileLoaderTests : IDisposable
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "resource-loader-" + Guid.NewGuid().ToString("N"));

        public FileLoaderTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        /// <summary> 记录每次转换收到的参数并返回带参数标记的产物 </summary>
        private sealed class RecordingConverter : IFileResourceConverter
        {
            public List<string> Seen { get; } = new List<string>();

            public int Count => Seen.Count;

            public Task<LoadedAsset> ConvertAsync(
                string path, Type resultType, string? conversionKey, CancellationToken cancellationToken)
            {
                lock (Seen) Seen.Add((conversionKey ?? "<null>") + "|" + Path.GetFileName(path));
                return Task.FromResult(new LoadedAsset(new FakeProduct(conversionKey ?? "<null>")));
            }
        }

        private string WriteFile(string name, string content)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        [Fact]
        public async Task 同地址不同转换参数各自转换且相同参数命中复用()
        {
            using var context = new TestExecutionContext();
            var converter = new RecordingConverter();
            var converters = new FileResourceConverters();
            converters.Register(typeof(FakeProduct), converter);
            var loader = new LocalFileLoader(converters);
            using var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics());
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var path = WriteFile("asset.bin", "payload");
            var small = new RequestOptions { Parameters = new FileLoadParameters("small") };
            var large = new RequestOptions { Parameters = new FileLoadParameters("large") };

            using (var first = await manager.LoadAsync<FakeProduct>(path, ResourceSource.Local, small)
                       .WaitAsync(TestTimeout))
            using (var second = await manager.LoadAsync<FakeProduct>(path, ResourceSource.Local, large)
                       .WaitAsync(TestTimeout))
            {
                Assert.Equal(2, converter.Count);
                Assert.Equal("small", context.Invoke(() => first.Value).Name);
                Assert.Equal("large", context.Invoke(() => second.Value).Name);
                // 同一路径、同一目标类型，转换参数不同不得合并成同一产物。
                Assert.Equal(2, manager.GetSnapshot().ResourceRows.Count);
            }

            // 重复 small：命中已有产物，不再调用转换器。
            using var repeated = await manager.LoadAsync<FakeProduct>(path, ResourceSource.Local, small)
                .WaitAsync(TestTimeout);
            Assert.Equal(2, converter.Count);
            Assert.Equal("small", context.Invoke(() => repeated.Value).Name);
            Assert.Equal(2, manager.GetSnapshot().ResourceRows.Count);
        }

        [Fact]
        public async Task 不传转换参数与空字符串使用同一身份()
        {
            using var context = new TestExecutionContext();
            var converter = new RecordingConverter();
            var converters = new FileResourceConverters();
            converters.Register(typeof(FakeProduct), converter);
            using var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics());
            manager.RegisterLoader(ResourceSource.Local, new LocalFileLoader(converters));
            await manager.InitializeAsync();

            var path = WriteFile("plain.bin", "payload");
            using var without = await manager.LoadAsync<FakeProduct>(path, ResourceSource.Local)
                .WaitAsync(TestTimeout);
            using var empty = await manager.LoadAsync<FakeProduct>(
                    path, ResourceSource.Local, new RequestOptions { Parameters = new FileLoadParameters("") })
                .WaitAsync(TestTimeout);

            Assert.Equal(1, converter.Count);
            Assert.Single(manager.GetSnapshot().ResourceRows);
        }

        [Fact]
        public async Task 未注册的目标类型在解析阶段失败且不读文件()
        {
            using var context = new TestExecutionContext();
            using var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics());
            manager.RegisterLoader(ResourceSource.Local, new LocalFileLoader(FileResourceConverters.CreateDefault()));
            await manager.InitializeAsync();

            var path = Path.Combine(_root, "absent.bin");
            var error = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                manager.LoadAsync<FakeProduct>(path, ResourceSource.Local));
            Assert.Equal(DiagnosticCodes.AssetUnsupportedRepresentation, error.Error.DiagnosticCode);
            Assert.Equal(LoadStage.Resolve, error.Error.Stage);
        }

        [Fact]
        public async Task 不支持的参数类型在解析阶段明确报错()
        {
            using var context = new TestExecutionContext();
            using var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics());
            manager.RegisterLoader(ResourceSource.Local, new LocalFileLoader(FileResourceConverters.CreateDefault()));
            await manager.InitializeAsync();

            var error = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                manager.LoadAsync<byte[]>(WriteFile("bytes.bin", "x"), ResourceSource.Local,
                    new RequestOptions { Parameters = "unsupported" }));
            Assert.Equal(DiagnosticCodes.LoaderResolveFailed, error.Error.DiagnosticCode);
        }

        [Fact]
        public async Task 本地文件缺失按文件错误码失败且随后可重试()
        {
            using var context = new TestExecutionContext();
            using var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics());
            manager.RegisterLoader(ResourceSource.Local, new LocalFileLoader(FileResourceConverters.CreateDefault()));
            await manager.InitializeAsync();

            var path = Path.Combine(_root, "later.txt");
            var error = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                manager.LoadAsync<string>(path, ResourceSource.Local).WaitAsync(TestTimeout));
            Assert.Equal(DiagnosticCodes.FileNotFound, error.Error.DiagnosticCode);

            File.WriteAllText(path, "now exists", new UTF8Encoding(false));
            using var reference = await manager.LoadAsync<string>(path, ResourceSource.Local).WaitAsync(TestTimeout);
            Assert.Equal("now exists", context.Invoke(() => reference.Value));
        }
    }
}
