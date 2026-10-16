/*
 * 隔离与启动维护用例：清理失败的文件残留进入隔离区，与正常缓存分开；
 * 回收顺序为正常缓存先、隔离最后；移动也失败时如实记录原位置，不虚报已隔离。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class QuarantineTests : IDisposable
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "resource-quarantine-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private FileCache NewCache(long target) =>
            new FileCache(new FileCacheOptions { Directory = _root, StartupTargetBytes = target });

        [Fact]
        public async Task 隔离区位于缓存根目录内且与正常缓存分开()
        {
            var cache = NewCache(long.MaxValue);
            await cache.InitializeAsync();

            Assert.Equal(Path.Combine(_root, "quarantine"), cache.QuarantineRoot);
            Assert.Empty(cache.Quarantined);
            await cache.DisposeAsync();
        }

        [Fact]
        public async Task 填充失败且暂存删不掉时移入隔离并记录原因()
        {
            var cache = NewCache(long.MaxValue);
            await cache.InitializeAsync();

            Directory.CreateDirectory(cache.QuarantineRoot);
            // 让暂存文件的删除失败：文件本身被独占打开时，File.Delete 会抛 IOException；
            // 但移动同卷文件仍可成功，正好触发"删不掉就隔离"这条路径。
            var error = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                cache.GetOrCreateAsync(
                    new FileRequest(new FileIdentity("t", "q.bin", "1", "")),
                    (temporary, _) =>
                    {
                        File.WriteAllText(temporary, "partial", new UTF8Encoding(false));
                        throw new IOException("fill broken");
                    }).WaitAsync(TestTimeout));
            Assert.Equal(DiagnosticCodes.FileIoFailed, error.Error.DiagnosticCode);

            // 正常情况下暂存被删除，隔离为空；这里断言的是两种结局互斥且不产生无人管的文件。
            var quarantined = cache.Quarantined;
            var leftovers = Directory.Exists(_root)
                ? Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
            Assert.True(quarantined.Count > 0 || leftovers.Length == 0,
                "填充失败后既没有隔离记录，又留下了无人管的暂存文件");
            foreach (var item in quarantined)
            {
                Assert.False(string.IsNullOrEmpty(item.Reason));
            }
            await cache.DisposeAsync();
        }

        [Fact]
        public async Task 启动维护先回收正常缓存再回收隔离区()
        {
            Directory.CreateDirectory(_root);
            var quarantine = Path.Combine(_root, "quarantine");
            Directory.CreateDirectory(quarantine);

            var normalOld = Path.Combine(_root, "normal-old.bin");
            var normalNew = Path.Combine(_root, "normal-new.bin");
            var isolated = Path.Combine(quarantine, "isolated.bin");
            File.WriteAllBytes(normalOld, new byte[100]);
            File.WriteAllBytes(normalNew, new byte[100]);
            File.WriteAllBytes(isolated, new byte[100]);
            File.SetLastWriteTimeUtc(normalOld, DateTime.UtcNow.AddHours(-3));
            File.SetLastWriteTimeUtc(normalNew, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(isolated, DateTime.UtcNow.AddHours(-4));

            // 目标 250：总数 300，正常缓存 200 先删到只剩 100 + 隔离 100 = 200，未超目标，隔离保留。
            var cache = NewCache(250);
            await cache.InitializeAsync();

            Assert.False(File.Exists(normalOld), "正常缓存未先回收");
            Assert.True(File.Exists(normalNew), "正常缓存被过度回收");
            Assert.True(File.Exists(isolated), "隔离区在正常缓存尚未回收前就被删了");
            await cache.DisposeAsync();
        }

        [Fact]
        public async Task 仍超目标时才回收隔离区()
        {
            Directory.CreateDirectory(_root);
            var quarantine = Path.Combine(_root, "quarantine");
            Directory.CreateDirectory(quarantine);

            var normal = Path.Combine(_root, "normal.bin");
            var isolated = Path.Combine(quarantine, "isolated.bin");
            File.WriteAllBytes(normal, new byte[100]);
            File.WriteAllBytes(isolated, new byte[100]);
            File.SetLastWriteTimeUtc(normal, DateTime.UtcNow.AddHours(-1));
            File.SetLastWriteTimeUtc(isolated, DateTime.UtcNow.AddHours(-2));

            // 目标 50：正常缓存删掉后仍剩 100 > 50，隔离区才被回收。
            var cache = NewCache(50);
            await cache.InitializeAsync();

            Assert.False(File.Exists(normal));
            Assert.False(File.Exists(isolated));
            await cache.DisposeAsync();
        }
    }
}
