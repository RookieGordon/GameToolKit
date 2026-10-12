/*
 * 资源系统 V2 单元测试 —— 核心共享流程、竞态、关闭与错误契约 (§14.2 C 系列 / §14.5 / §14.6 X01)。
 */

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public class ResourceStoreTests
    {
        private static LoadError CompleteError(string code)
        {
            return new LoadError(code, LoadStage.LoadAsset, CleanupStatus.Complete);
        }

        private static LoadError UnknownError(string code, Exception cause)
        {
            return new LoadError(code, LoadStage.LoadAsset, CleanupStatus.Unknown, cause);
        }

        [Fact]
        public async Task C01_SharedLoadOnce_20Waiters_Get20RefsAndOneLoad()
        {
            var manager = V2Test.NewManager(out var clock, out var diag);
            var loader = new GatedLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var tasks = Enumerable.Range(0, 20)
                .Select(_ => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local))
                .ToList();
            Assert.All(tasks, t => Assert.False(t.IsCompleted));

            loader.ReleaseGate("a");
            var refs = await Task.WhenAll(tasks);

            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(20, refs.Distinct().Count());
            Assert.All(refs, r => Assert.Same(refs[0].Value, r.Value));

            var snapshot = manager.GetSnapshot();
            var row = Assert.Single(snapshot.ResourceRows);
            Assert.Equal(20, row.HoldCount);

            foreach (var r in refs)
            {
                r.Dispose();
            }
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount);
            Assert.True(manager.GetSnapshot().IsDisposed);
        }

        [Fact]
        public async Task C02_IndependentOwners_DisposeARetainedBStillWorks()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var a = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            var b = a.Retain();

            a.Dispose();
            Assert.True(b.IsValid);
            Assert.NotNull(b.Value);

            b.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount); // 底层只释放一次
        }

        [Fact]
        public async Task C03_OldRefNeverRevives_AfterDisposeCannotAccessValue()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var a = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            a.Dispose();

            var b = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            a.Dispose(); // 幂等
            Assert.Throws<ObjectDisposedException>(() => a.Value);
            Assert.False(a.IsValid);

            var snapshot = manager.GetSnapshot();
            Assert.Equal(1, snapshot.ResourceRows[0].HoldCount); // B 持有数不受影响
            b.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task C04_DuplicateDisposeRace_OnlyReleasesOnce()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var a = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            var d1 = Task.Run(() => a.Dispose());
            var d2 = Task.Run(() => a.Dispose());
            await Task.WhenAll(d1, d2);

            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task C05_SourceIsolation_SameAddressDoesNotCrossUse()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loaderA = new FakeLoader { Factory = _ => new FakeAsset("from-a") };
            var loaderB = new FakeLoader { Factory = _ => new FakeAsset("from-b") };
            manager.RegisterLoader(ResourceSource.Local, loaderA);
            manager.RegisterLoader(ResourceSource.Remote, loaderB);
            await manager.InitializeAsync();

            var refA = await manager.LoadAsync<FakeAsset>("same", source: ResourceSource.Local);
            var refB = await manager.LoadAsync<FakeAsset>("same", source: ResourceSource.Remote);

            Assert.Equal(1, loaderA.LoadCount);
            Assert.Equal(1, loaderB.LoadCount);
            Assert.Equal("from-a", refA.Value.Name);
            Assert.Equal("from-b", refB.Value.Name);

            refA.Dispose();
            refB.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loaderA.ReleaseCount == 1 && loaderB.ReleaseCount == 1);
            Assert.Equal(1, loaderA.ReleaseCount);
            Assert.Equal(1, loaderB.ReleaseCount);
        }

        [Fact]
        public async Task C06_VersionIsolation_DifferentLocalKeysAreIndependentEntries()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader { KeyOf = a => a };
            loader.Factory = key => new FakeAsset("v:" + key);
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var v1 = await manager.LoadAsync<FakeAsset>("res", source: ResourceSource.Local);
            loader.KeyOf = a => a + "#v2"; // 模拟清单更新后解析到新版本
            var v2 = await manager.LoadAsync<FakeAsset>("res", source: ResourceSource.Local);

            Assert.NotSame(v1.Value, v2.Value);
            Assert.Equal(2, loader.LoadCount);
            Assert.Equal(2, manager.GetSnapshot().ResourceRows.Count);

            v1.Dispose();
            v2.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 2);
            Assert.Equal(2, loader.ReleaseCount);
        }

        [Fact]
        public async Task C07_ResultTypeMismatch_ThrowsAndReleasesResultOnce()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(
                () => manager.LoadAsync<string>("a", source: ResourceSource.Local)); // FakeAsset 不能赋给 string

            Assert.Equal(DiagnosticCodes.AssetTypeMismatch, ex.Error.DiagnosticCode);
            Assert.Equal(CleanupStatus.Complete, ex.Error.Cleanup);
            Assert.Equal(1, loader.LoadCount);

            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount); // 无人接收的结果释放一次
            Assert.Empty(manager.GetSnapshot().ResourceRows);
        }

        [Fact]
        public async Task C08_OneWaiterCancels_OtherStillSucceeds()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var ctsA = new CancellationTokenSource();
            var taskA = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local, cancellationToken: ctsA.Token);
            var taskB = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);

            ctsA.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => taskA);

            loader.ReleaseGate("a");
            var refB = await taskB;

            Assert.Equal(1, loader.LoadCount); // A 取消不中止共享加载
            refB.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task C09_AllWaitersCancel_LateSuccessIsReleasedExactlyOnce()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader { IgnoreCancellation = true }; // 后端不能立即取消
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var cts = new CancellationTokenSource();
            var task = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local, cancellationToken: cts.Token);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => task);

            loader.ReleaseGate("a"); // 迟到成功
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);

            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(1, loader.ReleaseCount); // 无业务交付，LoadedAsset 最终释放一次
        }

        [Fact]
        public async Task C11_NewRequestAfterAbandon_DoesNotJoinAbandonedOperation()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader { IgnoreCancellation = true };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var cts = new CancellationTokenSource();
            var taskA = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local, cancellationToken: cts.Token);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => taskA);

            var taskB = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local); // 旧操作未结束即再次请求
            loader.ReleaseGate("a");

            var refB = await taskB;
            Assert.Equal(2, loader.LoadCount); // 新请求不能接入已放弃操作
            refB.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 2);
            Assert.Equal(2, loader.ReleaseCount);
        }

        [Fact]
        public async Task C12_FailureThenRetry_SecondLoadSucceeds()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader
            {
                LoadErrors = new Queue<Exception>(
                    new[] { new ResourceLoadException(CompleteError(DiagnosticCodes.AssetLoadFailed)) })
            };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local));
            Assert.Equal(DiagnosticCodes.AssetLoadFailed, ex.Error.DiagnosticCode);

            var reference = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local); // 失败不留可命中项
            Assert.Equal(2, loader.LoadCount);
            reference.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task C13_IdleReuse_WithinLifetimeThenEvictedAtDeadline()
        {
            var manager = V2Test.NewManager(out var clock, out _,
                o => o.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.FromSeconds(10) });
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var a = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            var value = a.Value;
            a.Dispose();

            clock.Advance(9);
            manager.Tick();
            Assert.Equal(0, loader.ReleaseCount);

            var b = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local); // 保留期内复用
            Assert.Same(value, b.Value);
            Assert.Equal(1, loader.LoadCount);
            b.Dispose();

            clock.Advance(10.5);
            manager.Tick();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount); // 到期只释放一次
        }

        [Fact]
        public async Task C14_EvictionWithOwners_OnlyIdleEntriesAreEvicted()
        {
            var manager = V2Test.NewManager(out _, out _,
                o => o.Memory = new MemoryPolicy
                {
                    IdleLifetime = TimeSpan.FromSeconds(60),
                    MaxIdleEntries = 1,
                });
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var a = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            var b = await manager.LoadAsync<FakeAsset>("b", source: ResourceSource.Local);
            var valueA = a.Value;
            var valueB = b.Value;
            a.Dispose();   // a 进入空闲
            b.Dispose();   // b 进入空闲，超预算 → 最旧的 a 被回收

            Assert.Equal(1, loader.ReleaseCount); // 只淘汰空闲项 a
            var loadCountBefore = loader.LoadCount;
            var b2 = await manager.LoadAsync<FakeAsset>("b", source: ResourceSource.Local); // b 仍在空闲缓存，可复活
            Assert.Same(valueB, b2.Value);
            Assert.Equal(loadCountBefore, loader.LoadCount); // 复活不重新加载
            b2.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task C15_IdleDeadline_TickAt9sKeepsAt10sEvicts()
        {
            var manager = V2Test.NewManager(out var clock, out _,
                o => o.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.FromSeconds(10) });
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var a = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            a.Dispose();

            clock.Advance(9);
            manager.Tick();
            Assert.Equal(0, loader.ReleaseCount);

            clock.Advance(1);
            manager.Tick();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task C17_UnloadVsAcquire_NewRequestDuringUnloadIsIndependent()
        {
            var manager = V2Test.NewManager(out _, out _,
                o => o.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.Zero });
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var a = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            var firstValue = a.Value;
            a.Dispose(); // 立即卸载

            var b = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local); // 卸载期间新请求
            Assert.Equal(2, loader.LoadCount);
            Assert.NotSame(firstValue, b.Value); // 新记录是新的一次加载结果
            b.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 2);
            Assert.Equal(2, loader.ReleaseCount);
        }

        [Fact]
        public async Task C18_CloseDuringLoad_LateSuccessNotDelivered()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader { IgnoreCancellation = true };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var task = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            manager.Dispose();

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => task);
            Assert.Equal(DiagnosticCodes.LifecycleManagerDisposed, ex.Error.DiagnosticCode);

            loader.ReleaseGate("a"); // 迟到成功
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1
                && manager.GetSnapshot().ResourceRows.Count == 0);
            Assert.True(manager.GetSnapshot().IsDisposed);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
        }

        [Fact]
        public async Task C19_DisposeWithLiveOwner_InvalidatesReferenceAndReleasesWithoutReturn()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var a = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            manager.Dispose();

            Assert.False(a.IsValid);
            Assert.Throws<ObjectDisposedException>(() => a.Value);
            Assert.Throws<ObjectDisposedException>(() => a.Retain());
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            a.Dispose();

            manager.Dispose(); // 退出和旧凭证归还均幂等。
            Assert.Equal(1, loader.ReleaseCount);
            Assert.True(manager.GetSnapshot().IsDisposed);
        }

        [Fact]
        public async Task C20_ReleaseThrows_EntryQuarantinedAndKeyBlocked()
        {
            var manager = V2Test.NewManager(out _, out var diag,
                o => o.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.Zero });
            var brokenLoader = new BrokenReleaseLoader();
            manager.RegisterLoader(ResourceSource.Local, brokenLoader);
            await manager.InitializeAsync();

            var a = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            a.Dispose(); // 立即卸载 → 释放回调抛错 → 条目隔离

            // 同键重新加载返回原清理故障 (管理器仍在运行)
            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local));
            Assert.Equal(DiagnosticCodes.LifecycleReleaseFailed, ex.Error.DiagnosticCode);
            Assert.Equal(1, diag.CountByCode(DiagnosticCodes.LifecycleReleaseFailed));

            // 故障局限于该资源；退出不抛聚合异常，也不盲重试释放回调。
            manager.Dispose();
            Assert.True(manager.GetSnapshot().IsDisposed);
            Assert.Equal(1, brokenLoader.ReleaseCount);
        }

        private sealed class BrokenReleaseLoader : IResourceLoader
        {
            public int LoadCount;
            public int ReleaseCount;

            public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new ResolvedResource(request.Address, typeof(object)));
            }

            public Task<LoadedAsset> LoadAsync(
                ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
            {
                LoadCount++;
                return Task.FromResult(new LoadedAsset(
                    new FakeAsset("broken"), 100, null, Release));
            }

            private Task Release()
            {
                ReleaseCount++;
                return Task.FromException(new InvalidOperationException("release backend broken"));
            }
        }

        [Fact]
        public async Task X01_NullLoaderResult_FailsWaitersAndAllowsRetry()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader
            {
                ResultOverrides = new Queue<LoadedAsset?>(new LoadedAsset?[] { null })
            };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local));
            Assert.Equal(DiagnosticCodes.LoaderInvalidResult, ex.Error.DiagnosticCode);

            var reference = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local); // 条目已退出 Loading，可重试
            Assert.Equal(2, loader.LoadCount);
            reference.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task E04_UnknownPreservesCause_UnclassifiedExceptionMapped()
        {
            var manager = V2Test.NewManager(out _, out _);
            var cause = new InvalidOperationException("raw loader bug");
            var loader = new FakeLoader
            {
                LoadErrors = new Queue<Exception>(new[] { cause })
            };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local));
            Assert.Equal(DiagnosticCodes.InternalUnexpected, ex.Error.DiagnosticCode);
            Assert.Equal(CleanupStatus.Unknown, ex.Error.Cleanup);
            Assert.Same(cause, ex.Error.Cause);
            Assert.Same(cause, ex.InnerException);
        }

        [Fact]
        public async Task E05_SingleFinalLog_20WaitersShareOneDiagnosticId()
        {
            var manager = V2Test.NewManager(out _, out var diag);
            var loader = new GatedLoader
            {
                FailWith = new ResourceLoadException(CompleteError(DiagnosticCodes.AssetLoadFailed))
            };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var tasks = Enumerable.Range(0, 20).Select(_ => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local)).ToList();
            loader.ReleaseGate("a"); // 所有等待者已加入后共享操作才失败
            var exceptions = (await Task.WhenAll(tasks.Select(V2Test.Capture)))
                .Where(e => e != null)
                .Cast<ResourceLoadException>()
                .ToList();

            Assert.Equal(20, exceptions.Count);
            Assert.Single(exceptions.Select(e => e.Error.DiagnosticId).Distinct());
            Assert.Equal(1, diag.CountByCode(DiagnosticCodes.AssetLoadFailed)); // 一条最终失败日志
            Assert.Equal(1, loader.LoadCount);
            manager.Dispose();
        }

        [Fact]
        public async Task E06_CancelIsNotFailure_NoFaultLogForUserCancel()
        {
            var manager = V2Test.NewManager(out _, out var diag);
            var loader = new GatedLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var cts = new CancellationTokenSource();
            var task = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local, cancellationToken: cts.Token);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => task);

            loader.ReleaseGate("a");
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ActiveCount == 0);
            Assert.Equal(0, diag.ReportCount); // 主动取消不产生故障日志
        }

        [Fact]
        public async Task RequestTimeout_MapsToTimeoutErrorNotUserCancel()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var task = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local, options: new RequestOptions
            {
                Timeout = TimeSpan.FromMilliseconds(50)
            });
            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => task);
            Assert.Equal(DiagnosticCodes.RequestTimeout, ex.Error.DiagnosticCode);

            loader.ReleaseGate("a");
            manager.Dispose();
        }

        [Fact]
        public async Task Running_CannotReplaceRegisteredLoader()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var reference = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            Assert.Throws<InvalidOperationException>(() => manager.ReplaceLoader(ResourceSource.Local, new FakeLoader()));
            reference.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task MissingLoader_ThrowsNotRegistered()
        {
            var manager = V2Test.NewManager(out _, out _);
            await manager.InitializeAsync();
            var ex = await Assert.ThrowsAsync<ResourceLoadException>(
                () => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Remote));
            Assert.Equal(DiagnosticCodes.LoaderNotRegistered, ex.Error.DiagnosticCode);
        }

        [Fact]
        public async Task ReplaceLoader_InConfiguring_IsAllowed()
        {
            var manager = V2Test.NewManager(out _, out _);
            var first = new FakeLoader { Factory = _ => new FakeAsset("first") };
            var second = new FakeLoader { Factory = _ => new FakeAsset("second") };
            manager.RegisterLoader(ResourceSource.Local, first);
            manager.ReplaceLoader(ResourceSource.Local, second);
            await manager.InitializeAsync();

            var reference = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            Assert.Equal("second", reference.Value.Name);
            reference.Dispose();
            manager.Dispose();
        }
    }
}
