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
            manager.RegisterLoader("fake", loader);

            var tasks = Enumerable.Range(0, 20)
                .Select(_ => manager.LoadAsync<FakeAsset>("a"))
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
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount);
            Assert.Equal(ManagerState.Closed, manager.GetSnapshot().State);
        }

        [Fact]
        public async Task C02_IndependentOwners_DisposeARetainedBStillWorks()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader("fake", loader);

            var a = await manager.LoadAsync<FakeAsset>("a");
            var b = a.Retain();

            a.Dispose();
            Assert.True(b.IsValid);
            Assert.NotNull(b.Value);

            b.Dispose();
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount); // 底层只释放一次
        }

        [Fact]
        public async Task C03_OldRefNeverRevives_AfterDisposeCannotAccessValue()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader("fake", loader);

            var a = await manager.LoadAsync<FakeAsset>("a");
            a.Dispose();

            var b = await manager.LoadAsync<FakeAsset>("a");
            a.Dispose(); // 幂等
            Assert.Throws<ObjectDisposedException>(() => a.Value);
            Assert.False(a.IsValid);

            var snapshot = manager.GetSnapshot();
            Assert.Equal(1, snapshot.ResourceRows[0].HoldCount); // B 持有数不受影响
            b.Dispose();
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task C04_DuplicateDisposeRace_OnlyReleasesOnce()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader("fake", loader);

            var a = await manager.LoadAsync<FakeAsset>("a");
            var d1 = Task.Run(() => a.Dispose());
            var d2 = Task.Run(() => a.Dispose());
            await Task.WhenAll(d1, d2);

            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task C05_LoaderNamespaceIsolation_SameAddressDoesNotCrossUse()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loaderA = new FakeLoader { Factory = _ => new FakeAsset("from-a") };
            var loaderB = new FakeLoader { Factory = _ => new FakeAsset("from-b") };
            manager.RegisterLoader("a", loaderA);
            manager.RegisterLoader("b", loaderB);

            var refA = await manager.LoadAsync<FakeAsset>("same", loader: "a");
            var refB = await manager.LoadAsync<FakeAsset>("same", loader: "b");

            Assert.Equal(1, loaderA.LoadCount);
            Assert.Equal(1, loaderB.LoadCount);
            Assert.Equal("from-a", refA.Value.Name);
            Assert.Equal("from-b", refB.Value.Name);

            refA.Dispose();
            refB.Dispose();
            await manager.ShutdownAsync();
            Assert.Equal(1, loaderA.ReleaseCount);
            Assert.Equal(1, loaderB.ReleaseCount);
        }

        [Fact]
        public async Task C06_VersionIsolation_DifferentLocalKeysAreIndependentEntries()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader { KeyOf = a => a };
            loader.Factory = key => new FakeAsset("v:" + key);
            manager.RegisterLoader("fake", loader);

            var v1 = await manager.LoadAsync<FakeAsset>("res");
            loader.KeyOf = a => a + "#v2"; // 模拟清单更新后解析到新版本
            var v2 = await manager.LoadAsync<FakeAsset>("res");

            Assert.NotSame(v1.Value, v2.Value);
            Assert.Equal(2, loader.LoadCount);
            Assert.Equal(2, manager.GetSnapshot().ResourceRows.Count);

            v1.Dispose();
            v2.Dispose();
            await manager.ShutdownAsync();
            Assert.Equal(2, loader.ReleaseCount);
        }

        [Fact]
        public async Task C07_ResultTypeMismatch_ThrowsAndReleasesResultOnce()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader("fake", loader);

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(
                () => manager.LoadAsync<string>("a")); // FakeAsset 不能赋给 string

            Assert.Equal(DiagnosticCodes.AssetTypeMismatch, ex.Error.DiagnosticCode);
            Assert.Equal(CleanupStatus.Complete, ex.Error.Cleanup);
            Assert.Equal(1, loader.LoadCount);

            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount); // 无人接收的结果释放一次
            Assert.Empty(manager.GetSnapshot().ResourceRows);
        }

        [Fact]
        public async Task C08_OneWaiterCancels_OtherStillSucceeds()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader();
            manager.RegisterLoader("fake", loader);

            var ctsA = new CancellationTokenSource();
            var taskA = manager.LoadAsync<FakeAsset>("a", cancellationToken: ctsA.Token);
            var taskB = manager.LoadAsync<FakeAsset>("a");

            ctsA.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => taskA);

            loader.ReleaseGate("a");
            var refB = await taskB;

            Assert.Equal(1, loader.LoadCount); // A 取消不中止共享加载
            refB.Dispose();
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task C09_AllWaitersCancel_LateSuccessIsReleasedExactlyOnce()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader { IgnoreCancellation = true }; // 后端不能立即取消
            manager.RegisterLoader("fake", loader);

            var cts = new CancellationTokenSource();
            var task = manager.LoadAsync<FakeAsset>("a", cancellationToken: cts.Token);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => task);

            loader.ReleaseGate("a"); // 迟到成功
            await manager.ShutdownAsync();

            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(1, loader.ReleaseCount); // 无业务交付，LoadedAsset 最终释放一次
        }

        [Fact]
        public async Task C11_NewRequestAfterAbandon_DoesNotJoinAbandonedOperation()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader { IgnoreCancellation = true };
            manager.RegisterLoader("fake", loader);

            var cts = new CancellationTokenSource();
            var taskA = manager.LoadAsync<FakeAsset>("a", cancellationToken: cts.Token);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => taskA);

            var taskB = manager.LoadAsync<FakeAsset>("a"); // 旧操作未结束即再次请求
            loader.ReleaseGate("a");

            var refB = await taskB;
            Assert.Equal(2, loader.LoadCount); // 新请求不能接入已放弃操作
            refB.Dispose();
            await manager.ShutdownAsync();
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
            manager.RegisterLoader("fake", loader);

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => manager.LoadAsync<FakeAsset>("a"));
            Assert.Equal(DiagnosticCodes.AssetLoadFailed, ex.Error.DiagnosticCode);

            var reference = await manager.LoadAsync<FakeAsset>("a"); // 失败不留可命中项
            Assert.Equal(2, loader.LoadCount);
            reference.Dispose();
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task C13_IdleReuse_WithinLifetimeThenEvictedAtDeadline()
        {
            var manager = V2Test.NewManager(out var clock, out _,
                o => o.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.FromSeconds(10) });
            var loader = new FakeLoader();
            manager.RegisterLoader("fake", loader);

            var a = await manager.LoadAsync<FakeAsset>("a");
            var value = a.Value;
            a.Dispose();

            clock.Advance(9);
            manager.Tick();
            Assert.Equal(0, loader.ReleaseCount);

            var b = await manager.LoadAsync<FakeAsset>("a"); // 保留期内复用
            Assert.Same(value, b.Value);
            Assert.Equal(1, loader.LoadCount);
            b.Dispose();

            clock.Advance(10.5);
            manager.Tick();
            await manager.ShutdownAsync();
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
            manager.RegisterLoader("fake", loader);

            var a = await manager.LoadAsync<FakeAsset>("a");
            var b = await manager.LoadAsync<FakeAsset>("b");
            var valueA = a.Value;
            var valueB = b.Value;
            a.Dispose();   // a 进入空闲
            b.Dispose();   // b 进入空闲，超预算 → 最旧的 a 被回收

            Assert.Equal(1, loader.ReleaseCount); // 只淘汰空闲项 a
            var loadCountBefore = loader.LoadCount;
            var b2 = await manager.LoadAsync<FakeAsset>("b"); // b 仍在空闲缓存，可复活
            Assert.Same(valueB, b2.Value);
            Assert.Equal(loadCountBefore, loader.LoadCount); // 复活不重新加载
            b2.Dispose();
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task C15_IdleDeadline_TickAt9sKeepsAt10sEvicts()
        {
            var manager = V2Test.NewManager(out var clock, out _,
                o => o.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.FromSeconds(10) });
            var loader = new FakeLoader();
            manager.RegisterLoader("fake", loader);

            var a = await manager.LoadAsync<FakeAsset>("a");
            a.Dispose();

            clock.Advance(9);
            manager.Tick();
            Assert.Equal(0, loader.ReleaseCount);

            clock.Advance(1);
            manager.Tick();
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task C17_UnloadVsAcquire_NewRequestDuringUnloadIsIndependent()
        {
            var manager = V2Test.NewManager(out _, out _,
                o => o.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.Zero });
            var loader = new FakeLoader();
            manager.RegisterLoader("fake", loader);

            var a = await manager.LoadAsync<FakeAsset>("a");
            var firstValue = a.Value;
            a.Dispose(); // 立即卸载

            var b = await manager.LoadAsync<FakeAsset>("a"); // 卸载期间新请求
            Assert.Equal(2, loader.LoadCount);
            Assert.NotSame(firstValue, b.Value); // 新记录是新的一次加载结果
            b.Dispose();
            await manager.ShutdownAsync();
            Assert.Equal(2, loader.ReleaseCount);
        }

        [Fact]
        public async Task C18_CloseDuringLoad_LateSuccessNotDelivered()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader();
            manager.RegisterLoader("fake", loader);

            var task = manager.LoadAsync<FakeAsset>("a");
            manager.Dispose(); // 启动排空

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => task);
            Assert.Equal(DiagnosticCodes.LifecycleManagerClosing, ex.Error.DiagnosticCode);

            loader.ReleaseGate("a"); // 迟到成功
            await manager.ShutdownAsync();
            Assert.Equal(ManagerState.Closed, manager.GetSnapshot().State);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
        }

        [Fact]
        public async Task C19_CloseWithLiveOwner_WaitsForReturnThenCompletes()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader("fake", loader);

            var a = await manager.LoadAsync<FakeAsset>("a");
            manager.Dispose();

            using var waitCts = new CancellationTokenSource(100);
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => manager.ShutdownAsync(waitCts.Token)); // 活跃持有阻塞关闭完成

            Assert.True(a.IsValid); // 不提前销毁在用资源
            a.Dispose();

            await manager.ShutdownAsync(); // 归还后完成关闭
            Assert.Equal(1, loader.ReleaseCount);
            Assert.Equal(ManagerState.Closed, manager.GetSnapshot().State);
        }

        [Fact]
        public async Task C20_ReleaseThrows_EntryQuarantinedAndKeyBlocked()
        {
            var manager = V2Test.NewManager(out _, out var diag,
                o => o.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.Zero });
            var brokenLoader = new BrokenReleaseLoader();
            manager.RegisterLoader("fake", brokenLoader);

            var a = await manager.LoadAsync<FakeAsset>("a");
            a.Dispose(); // 立即卸载 → 释放回调抛错 → 条目隔离

            // 同键重新加载返回原清理故障 (管理器仍在运行)
            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => manager.LoadAsync<FakeAsset>("a"));
            Assert.Equal(DiagnosticCodes.LifecycleReleaseFailed, ex.Error.DiagnosticCode);
            Assert.Equal(1, diag.CountByCode(DiagnosticCodes.LifecycleReleaseFailed));

            // 关闭以清理故障结束 (Faulted)，回调不盲重试
            await Assert.ThrowsAsync<AggregateException>(() => manager.ShutdownAsync());
            Assert.Equal(ManagerState.Faulted, manager.GetSnapshot().State);
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
            manager.RegisterLoader("fake", loader);

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => manager.LoadAsync<FakeAsset>("a"));
            Assert.Equal(DiagnosticCodes.LoaderInvalidResult, ex.Error.DiagnosticCode);

            var reference = await manager.LoadAsync<FakeAsset>("a"); // 条目已退出 Loading，可重试
            Assert.Equal(2, loader.LoadCount);
            reference.Dispose();
            await manager.ShutdownAsync();
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
            manager.RegisterLoader("fake", loader);

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => manager.LoadAsync<FakeAsset>("a"));
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
            manager.RegisterLoader("fake", loader);

            var tasks = Enumerable.Range(0, 20).Select(_ => manager.LoadAsync<FakeAsset>("a")).ToList();
            loader.ReleaseGate("a"); // 所有等待者已加入后共享操作才失败
            var exceptions = (await Task.WhenAll(tasks.Select(V2Test.Capture)))
                .Where(e => e != null)
                .Cast<ResourceLoadException>()
                .ToList();

            Assert.Equal(20, exceptions.Count);
            Assert.Single(exceptions.Select(e => e.Error.DiagnosticId).Distinct());
            Assert.Equal(1, diag.CountByCode(DiagnosticCodes.AssetLoadFailed)); // 一条最终失败日志
            Assert.Equal(1, loader.LoadCount);
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task E06_CancelIsNotFailure_NoFaultLogForUserCancel()
        {
            var manager = V2Test.NewManager(out _, out var diag);
            var loader = new GatedLoader();
            manager.RegisterLoader("fake", loader);

            var cts = new CancellationTokenSource();
            var task = manager.LoadAsync<FakeAsset>("a", cancellationToken: cts.Token);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => task);

            loader.ReleaseGate("a");
            await manager.ShutdownAsync();
            Assert.Equal(0, diag.ReportCount); // 主动取消不产生故障日志
        }

        [Fact]
        public async Task RequestTimeout_MapsToTimeoutErrorNotUserCancel()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader();
            manager.RegisterLoader("fake", loader);

            var task = manager.LoadAsync<FakeAsset>("a", options: new RequestOptions
            {
                Timeout = TimeSpan.FromMilliseconds(50)
            });
            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => task);
            Assert.Equal(DiagnosticCodes.RequestTimeout, ex.Error.DiagnosticCode);

            loader.ReleaseGate("a");
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task RegistryFreeze_CannotRegisterAfterFirstRequest()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader("fake", loader);

            var reference = await manager.LoadAsync<FakeAsset>("a");
            Assert.Throws<InvalidOperationException>(() => manager.RegisterLoader("other", new FakeLoader()));
            reference.Dispose();
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task MissingLoader_ThrowsNotRegistered()
        {
            var manager = V2Test.NewManager(out _, out _);
            var ex = await Assert.ThrowsAsync<ResourceLoadException>(
                () => manager.LoadAsync<FakeAsset>("a", loader: "nope"));
            Assert.Equal(DiagnosticCodes.LoaderNotRegistered, ex.Error.DiagnosticCode);
        }

        [Fact]
        public async Task ReplaceLoader_InConfiguring_IsAllowed()
        {
            var manager = V2Test.NewManager(out _, out _);
            var first = new FakeLoader { Factory = _ => new FakeAsset("first") };
            var second = new FakeLoader { Factory = _ => new FakeAsset("second") };
            manager.RegisterLoader("fake", first);
            manager.ReplaceLoader("fake", second);

            var reference = await manager.LoadAsync<FakeAsset>("a");
            Assert.Equal("second", reference.Value.Name);
            reference.Dispose();
            await manager.ShutdownAsync();
        }
    }
}
