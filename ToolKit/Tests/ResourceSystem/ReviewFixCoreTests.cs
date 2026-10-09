/*
 * Review 修复回归测试 —— 核心 / 实例池 / 绑定层。
 * 覆盖 R07/R08/R09/R21/R25/R26/R27/R29/R30/R31/R32/R34 与 C17 增强口径。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public class ReviewFixCoreTests
    {
        private static ResourceManager NewRentedManager(
            out FakeLoader loader,
            out FakeInstanceFactory factory,
            Action<ResourceSystemOptions>? configure = null)
        {
            var manager = V2Test.NewManager(out _, out _, configure);
            loader = new FakeLoader { Factory = _ => new FakePrefab("proto") };
            factory = new FakeInstanceFactory();
            manager.RegisterLoader("fake", loader);
            manager.RegisterFactory("fakeFactory", factory);
            return manager;
        }

        private static Task<InstanceLease<FakeInstance>> RentAsync(ResourceManager manager, string address = "enemy")
        {
            return manager.RentAsync<FakeInstance>(address, loader: "fake", factory: "fakeFactory");
        }

        // ---- R07: 关闭期间创建失败(Incomplete) 不再绕过清理语义 ----
        [Fact]
        public async Task R07_CreateFailsIncompleteDuringClear_PoolCloseReportsFault()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            var createGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gatedFactory = new GatedCreateFactory(factory, createGate)
            {
                FailWith = new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate, CleanupStatus.Incomplete)),
            };
            manager.RegisterFactory("gatedFactory", gatedFactory);

            var rentTask = manager.RentAsync<FakeInstance>("enemy", loader: "fake", factory: "gatedFactory");
            var clearTask = manager.ClearPoolAsync("enemy", loader: "fake", factory: "gatedFactory");

            createGate.TrySetResult(true); // 关闭期间创建以 Incomplete 失败

            var clearEx = await Assert.ThrowsAnyAsync<ResourceLoadException>(() => clearTask);
            Assert.Equal(CleanupStatus.Incomplete, clearEx.Error.Cleanup); // 不再报告虚假的安全关闭
            var rentEx = await Assert.ThrowsAsync<ResourceLoadException>(() => rentTask);
            Assert.True(
                rentEx.Error.DiagnosticCode == DiagnosticCodes.InstanceCreateFailed
                || rentEx.Error.DiagnosticCode == DiagnosticCodes.InstancePoolClosed,
                $"等待者应被结算而非悬挂: {rentEx.Error.DiagnosticCode}");
            await manager.ShutdownAsync().ContinueWith(_ => { });
        }

        private sealed class GatedCreateFactory : IInstanceFactory
        {
            private readonly FakeInstanceFactory _inner;
            private readonly TaskCompletionSource<bool> _gate;
            public Exception? FailWith;

            public GatedCreateFactory(FakeInstanceFactory inner, TaskCompletionSource<bool> gate)
            {
                _inner = inner;
                _gate = gate;
            }

            public ResourceRequest GetPrototypeRequest(ResourceRequest instanceRequest) => _inner.GetPrototypeRequest(instanceRequest);
            public string GetInstanceKey(ResourceRequest instanceRequest) => _inner.GetInstanceKey(instanceRequest);
            public bool CanCreate(object prototype, Type instanceType) => _inner.CanCreate(prototype, instanceType);
            public bool IsAlive(object instance) => _inner.IsAlive(instance);
            public void OnRent(object instance) => _inner.OnRent(instance);
            public void OnReturn(object instance) => _inner.OnReturn(instance);
            public Task DestroyAsync(object instance) => _inner.DestroyAsync(instance);

            public async Task<object> CreateAsync(object prototype, ResourceRequest creationRequest, CancellationToken operationToken)
            {
                await _gate.Task.ConfigureAwait(false);
                if (FailWith != null)
                {
                    throw FailWith;
                }
                return await _inner.CreateAsync(prototype, creationRequest, operationToken).ConfigureAwait(false);
            }
        }

        // ---- R08: 无人等待的迟到实例走与普通归还一致的状态流 ----
        [Fact]
        public async Task R08_LateInstanceWithReentrantClear_StillTrackedAndDestroyed()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            var returnGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? clearObserved = null;
            var reentrant = new ReentrantReturnFactory(factory, () =>
            {
                // OnReturn 内同步关闭池 (用户脚本在归还过程中关闭)；不等待其完成
                clearObserved = manager.ClearPoolAsync("enemy", loader: "fake", factory: "reentrant");
                return true;
            }, returnGate);
            manager.RegisterFactory("reentrant", reentrant);

            var lease = await manager.RentAsync<FakeInstance>("enemy", loader: "fake", factory: "reentrant");
            lease.Dispose(); // 归还触发 OnReturn → 重入 ClearPool

            for (var i = 0; i < 100 && factory.DestroyCount < 1; i++)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }
            var rows = manager.GetSnapshot().PoolRows;
            var clearInfo = clearObserved == null ? "not-called" : $"{clearObserved.Status}";
            if (clearObserved is { IsFaulted: true } faulted)
            {
                clearInfo += ":" + faulted.Exception!.GetBaseException().Message;
            }
            Assert.True(factory.DestroyCount == 1,
                $"实例应被销毁而非失去追踪: destroy={factory.DestroyCount}, clear={clearInfo}, " +
                $"pools=[{string.Join(';', rows.ConvertAll(r => $"{r.State}:{r.Active}/{r.Idle}/{r.InFlight}"))}]");
            await manager.ShutdownAsync();
        }

        private sealed class ReentrantReturnFactory : IInstanceFactory
        {
            private readonly FakeInstanceFactory _inner;
            private readonly Func<bool> _onReturnAction;
            private readonly TaskCompletionSource<bool> _gate;

            public ReentrantReturnFactory(FakeInstanceFactory inner, Func<bool> onReturnAction, TaskCompletionSource<bool> gate)
            {
                _inner = inner;
                _onReturnAction = onReturnAction;
                _gate = gate;
            }

            public ResourceRequest GetPrototypeRequest(ResourceRequest instanceRequest) => _inner.GetPrototypeRequest(instanceRequest);
            public string GetInstanceKey(ResourceRequest instanceRequest) => _inner.GetInstanceKey(instanceRequest);
            public bool CanCreate(object prototype, Type instanceType) => _inner.CanCreate(prototype, instanceType);
            public bool IsAlive(object instance) => _inner.IsAlive(instance);
            public void OnRent(object instance) => _inner.OnRent(instance);

            public void OnReturn(object instance)
            {
                _onReturnAction();
                _inner.OnReturn(instance);
            }

            public Task<object> CreateAsync(object prototype, ResourceRequest creationRequest, CancellationToken operationToken)
                => _inner.CreateAsync(prototype, creationRequest, operationToken);
            public Task DestroyAsync(object instance) => _inner.DestroyAsync(instance);
        }

        // ---- R09: CanCreate 抛异常不再悬挂，原型引用被归还 ----
        [Fact]
        public async Task R09_CanCreateThrows_RentFailsFastAndPrototypeReleased()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            factory.ThrowOnCanCreate = new InvalidOperationException("CanCreate broken");

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(DiagnosticCodes.InstanceUnsupported, ex.Error.DiagnosticCode);
            Assert.NotNull(ex.Error.Cause);

            // 原型引用已归还，桶已移除，关闭可正常完成
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount);
        }

        // ---- R21: LoadError 默认 Unknown；关联清理失败升级总体清理状态 ----
        [Fact]
        public void R21_LoadErrorDefaultsToUnknownAndRelatedEscalates()
        {
            var error = new LoadError(DiagnosticCodes.AssetLoadFailed, LoadStage.LoadAsset);
            Assert.Equal(CleanupStatus.Unknown, error.Cleanup);

            var complete = new LoadError(DiagnosticCodes.AssetLoadFailed, LoadStage.LoadAsset, CleanupStatus.Complete);
            var rollback = new LoadError(DiagnosticCodes.LifecycleReleaseFailed, LoadStage.ReleaseAsset, CleanupStatus.Incomplete);
            var escalated = complete.WithRelated(rollback);
            Assert.Equal(CleanupStatus.Incomplete, escalated.Cleanup);
            Assert.Single(escalated.RelatedErrors);
            Assert.Equal(complete.DiagnosticId, escalated.DiagnosticId); // 主错误标识不变
        }

        // ---- R25: 加载器独立内存策略 MaxIdleEntries=0 表示不保留空闲资源 ----
        [Fact]
        public async Task R25_LoaderMemoryPolicyZeroIdle_ReleasesOnReturn()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader("noidle", loader, new LoaderPolicy
            {
                Memory = new MemoryPolicy { IdleLifetime = TimeSpan.FromSeconds(60), MaxIdleEntries = 0 },
            });

            var reference = await manager.LoadAsync<FakeAsset>("a", loader: "noidle");
            reference.Dispose();
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount); // 归零即卸载，不再缓存
        }

        // ---- R26: IsAlive 查询失败不当作已销毁，实例按销毁协议清理 ----
        [Fact]
        public async Task R26_IsAliveThrows_AfterCreate_InstanceDestroyedNotDropped()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var aliveFactory = new FlakyAliveFactory(factory, gate);
            manager.RegisterFactory("flaky", aliveFactory);

            var rentTask = manager.RentAsync<FakeInstance>("enemy", loader: "fake", factory: "flaky");
            aliveFactory.FailNextIsAlive = true; // 创建完成后首次存活查询失败
            gate.TrySetResult(true);

            var captured = await V2Test.Capture(rentTask);
            var ex = captured as ResourceLoadException;
            Assert.True(ex != null,
                $"应抛 RLE: got={captured?.GetType().Name}, aliveCalls={aliveFactory.AliveCalls}, " +
                $"flagNow={aliveFactory.FailNextIsAlive}, destroy={factory.DestroyCount}, create={factory.CreateCount}");
            Assert.Equal(DiagnosticCodes.InstanceCreateFailed, ex!.Error.DiagnosticCode);
            Assert.Equal(1, factory.DestroyCount); // 保留记录并按销毁协议清理，不静默丢弃

            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount);
        }

        private sealed class FlakyAliveFactory : IInstanceFactory
        {
            private readonly FakeInstanceFactory _inner;
            private readonly TaskCompletionSource<bool> _createGate;
            public bool FailNextIsAlive;

            public FlakyAliveFactory(FakeInstanceFactory inner, TaskCompletionSource<bool> createGate)
            {
                _inner = inner;
                _createGate = createGate;
            }

            public ResourceRequest GetPrototypeRequest(ResourceRequest instanceRequest) => _inner.GetPrototypeRequest(instanceRequest);
            public string GetInstanceKey(ResourceRequest instanceRequest) => _inner.GetInstanceKey(instanceRequest);
            public bool CanCreate(object prototype, Type instanceType) => _inner.CanCreate(prototype, instanceType);
            public void OnRent(object instance) => _inner.OnRent(instance);
            public void OnReturn(object instance) => _inner.OnReturn(instance);
            public Task DestroyAsync(object instance) => _inner.DestroyAsync(instance);

            public async Task<object> CreateAsync(object prototype, ResourceRequest creationRequest, CancellationToken operationToken)
            {
                var instance = await _inner.CreateAsync(prototype, creationRequest, operationToken).ConfigureAwait(false);
                await _createGate.Task.ConfigureAwait(false);
                return instance;
            }

            public int AliveCalls;

            public bool IsAlive(object instance)
            {
                Interlocked.Increment(ref AliveCalls);
                if (FailNextIsAlive)
                {
                    FailNextIsAlive = false;
                    throw new InvalidOperationException("IsAlive broken");
                }
                return _inner.IsAlive(instance);
            }
        }

        // ---- R27: 旧代池销毁失败时，同 PoolKey 屏障阻止新租用 (即使新桶已存在) ----
        [Fact]
        public async Task R27_FaultBarrier_BlocksNewRentAfterNewGenerationExists()
        {
            var manager = NewRentedManager(out var loader, out var factory);

            var oldLease = await RentAsync(manager);
            var clearTask = manager.ClearPoolAsync("enemy", loader: "fake", factory: "fakeFactory");

            var newLease = await RentAsync(manager); // 新代际桶：旧桶 Closing + 新桶 Open
            Assert.Equal(2, manager.GetSnapshot().PoolRows.FindAll(p => p.State != PoolState.Closed).Count);

            factory.ThrowOnDestroy = new InvalidOperationException("destroy broken");
            oldLease.Dispose(); // 旧代归还销毁失败 → 同键故障屏障

            var third = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(DiagnosticCodes.InstanceDestroyFailed, third.Error.DiagnosticCode);

            newLease.Dispose();
        }

        // ---- R29: 省略加载器策略时默认四路并发 ----
        [Fact]
        public async Task R29_DefaultLoaderPolicy_IsFourConcurrentLoads()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader(); // 常规注册：不带策略
            manager.RegisterLoader("fake", loader);

            var tasks = new Task<ResourceRef<FakeAsset>>[8];
            for (var i = 0; i < 8; i++)
            {
                tasks[i] = manager.LoadAsync<FakeAsset>("res-" + i);
            }
            for (var i = 0; i < 8; i++)
            {
                loader.ReleaseGate("res-" + i);
            }
            var refs = await Task.WhenAll(tasks);
            Assert.Equal(4, loader.MaxActiveCount); // 缺省注册项采用默认四路并发
            foreach (var reference in refs)
            {
                reference.Dispose();
            }
            await manager.ShutdownAsync();
        }

        // ---- R30: 注入的用户错误映射器在交付边界生效 ----
        [Fact]
        public async Task R30_CustomErrorMapper_AppliedAtDelivery()
        {
            var mapper = new ForceStorageFullMapper();
            var manager = new ResourceManager(ImmediateExecutionContext.Instance,
                new ResourceSystemOptions { DefaultLoader = "missing" }, mapper, null);

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => manager.LoadAsync<FakeAsset>("a"));
            Assert.Equal(UserErrorCode.StorageFull, ex.Error.UserCode); // 用户类别被替换
            Assert.Equal(DiagnosticCodes.LoaderNotRegistered, ex.Error.DiagnosticCode); // 诊断证据不变
            Assert.Equal(1, mapper.CallCount);
        }

        private sealed class ForceStorageFullMapper : IErrorMapper
        {
            public int CallCount;

            public UserErrorCode Map(string diagnosticCode, LoadStage stage, IReadOnlyDictionary<string, object> context)
            {
                Interlocked.Increment(ref CallCount);
                return UserErrorCode.StorageFull;
            }
        }

        // ---- R31: 重复注册明确失败；显式替换可用 ----
        [Fact]
        public async Task R31_DuplicateRegisterRejected_ReplaceExplicit()
        {
            var manager = V2Test.NewManager(out _, out _);
            manager.RegisterLoader("fake", new FakeLoader());
            manager.RegisterFactory("f", new FakeInstanceFactory());
            Assert.Throws<InvalidOperationException>(() => manager.RegisterLoader("fake", new FakeLoader()));
            Assert.Throws<InvalidOperationException>(() => manager.RegisterFactory("f", new FakeInstanceFactory()));

            manager.ReplaceLoader("fake", new FakeLoader { Factory = _ => new FakeAsset("replaced") });
            var reference = await manager.LoadAsync<FakeAsset>("a");
            Assert.Equal("replaced", reference.Value.Name);
            reference.Dispose();
            await manager.ShutdownAsync();
        }

        // ---- R32: UnloadUnusedAsync 取消只中断等待，底层卸载继续 ----
        [Fact]
        public async Task R32_UnloadUnused_CancelReturnsImmediately_BackendContinues()
        {
            var manager = V2Test.NewManager(out _, out _,
                o => o.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.FromSeconds(9999) });
            var loader = new SlowReleaseLoader();
            manager.RegisterLoader("fake", loader);

            var reference = await manager.LoadAsync<FakeAsset>("a");
            reference.Dispose(); // 进入空闲缓存 (尚未卸载)

            using var cts = new CancellationTokenSource(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.UnloadUnusedAsync(cts.Token));

            loader.ReleaseGate.TrySetResult(true); // 底层卸载继续并只执行一次
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount);
        }

        private sealed class SlowReleaseLoader : IResourceLoader
        {
            public int ReleaseCount;
            public int LoadCountOf;
            public readonly TaskCompletionSource<bool> ReleaseGate =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
                => Task.FromResult(new ResolvedResource(request.Address, typeof(object)));

            public async Task<LoadedAsset> LoadAsync(
                ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
            {
                Interlocked.Increment(ref LoadCountOf);
                var value = new FakeAsset(resource.LocalKey);
                return new LoadedAsset(value, 1, null, async () =>
                {
                    await ReleaseGate.Task.ConfigureAwait(false);
                    Interlocked.Increment(ref ReleaseCount);
                });
            }
        }

        // ---- C17 增强口径：卸载未结束时同地址新请求 ----
        [Fact]
        public async Task C17_Strong_UnloadGateHeld_SecondLoadUsesNewEntry()
        {
            var manager = V2Test.NewManager(out _, out _,
                o => o.Memory = new MemoryPolicy { IdleLifetime = TimeSpan.Zero });
            var loader = new SlowReleaseLoader();
            manager.RegisterLoader("fake", loader);

            var first = await manager.LoadAsync<FakeAsset>("a");
            var firstValue = first.Value;
            first.Dispose(); // 卸载启动但被门控挂起

            var secondTask = manager.LoadAsync<FakeAsset>("a"); // 旧卸载未结束：等待同键屏障
            loader.ReleaseGate.TrySetResult(true);
            var second = await secondTask;
            Assert.NotSame(firstValue, second.Value);
            Assert.Equal(2, loader.LoadCountOf); // 屏障结束后新记录是新的一次加载

            second.Dispose();
            await manager.ShutdownAsync();
            Assert.Equal(2, loader.ReleaseCount);
        }

        // ---- R34: 绑定层 E09/E10/E11/E13 ----
        private sealed class RecordingTarget
        {
            public string? Current;
            public string? Second;
            public int ReplacedCount;
        }

        private sealed class RecordingApplicator : IResourceApplicator<RecordingTarget, FakeAsset>
        {
            public Exception? ThrowOnReplace;

            public void Replace(RecordingTarget target, FakeAsset value)
            {
                var old = target.Current;
                try
                {
                    if (ThrowOnReplace != null)
                    {
                        throw ThrowOnReplace; // 模拟属性设置失败
                    }
                    target.Current = value.Name;
                    target.ReplacedCount++;
                }
                catch
                {
                    target.Current = old; // 异常安全：恢复旧值
                    throw;
                }
            }

            public void Revert(RecordingTarget target)
            {
                target.Current = null;
            }
        }

        [Fact]
        public async Task E09_LatestBindingWins_SupersededNotApplied()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader();
            manager.RegisterLoader("fake", loader);
            var binder = new ResourceBinder(manager);
            var target = new RecordingTarget();
            var applicator = new RecordingApplicator();

            var applyA = binder.ApplyAsync(target, "sprite", "a", applicator, loader: "fake");
            var applyB = binder.ApplyAsync(target, "sprite", "b", applicator, loader: "fake");
            loader.ReleaseGate("a");
            loader.ReleaseGate("b");

            var resultB = await applyB; // B 后发：生效
            var resultA = await applyA; // A 先完成也已被作废
            Assert.Equal(BindingResult.Applied, resultB);
            Assert.Equal(BindingResult.Superseded, resultA);
            Assert.Equal("b", target.Current);
        }

        [Fact]
        public async Task E10_ApplyFailure_KeepsOldDisplay()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader { Factory = a => new FakeAsset(a) };
            manager.RegisterLoader("fake", loader);
            var binder = new ResourceBinder(manager);
            var target = new RecordingTarget();
            var applicator = new RecordingApplicator();

            await binder.ApplyAsync(target, "sprite", "a", applicator, loader: "fake");
            Assert.Equal("a", target.Current);

            applicator.ThrowOnReplace = new InvalidOperationException("apply failed");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => binder.ApplyAsync(target, "sprite", "b", applicator, loader: "fake"));
            Assert.Equal("a", target.Current); // 旧展示保留

            applicator.ThrowOnReplace = null;
            await binder.ApplyAsync(target, "sprite", "b", applicator, loader: "fake");
            Assert.Equal("b", target.Current);
            binder.Revert(target, "sprite", applicator);
            await manager.ShutdownAsync();
        }

        [Fact]
        public async Task E11_UnbindOrder_RevertClearsBeforeDispose()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader { Factory = a => new FakeAsset(a) };
            manager.RegisterLoader("fake", loader);
            var binder = new ResourceBinder(manager);
            var target = new RecordingTarget();
            var applicator = new RecordingApplicator();

            await binder.ApplyAsync(target, "sprite", "a", applicator, loader: "fake");
            var snapshot = manager.GetSnapshot();
            Assert.Equal(1, snapshot.ResourceRows[0].HoldCount); // slot 持有

            binder.Revert(target, "sprite", applicator);
            Assert.Null(target.Current); // 先清属性
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount); // 底层释放发生在解绑之后
        }

        [Fact]
        public async Task E13_IndependentSlots_SameTarget()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader { Factory = a => new FakeAsset(a) };
            manager.RegisterLoader("fake", loader);
            var binder = new ResourceBinder(manager);
            var target = new RecordingTarget();
            var applicator = new RecordingApplicator();
            var slot2Applicator = new SecondSlotApplicator();

            await binder.ApplyAsync(target, "sprite", "a", applicator, loader: "fake");
            await binder.ApplyAsync(target, "material", "b", slot2Applicator, loader: "fake");

            binder.Revert(target, "sprite", applicator);
            Assert.Null(target.Current);            // sprite 槽被解除
            Assert.Equal("b", target.Second);       // 另一 slot (material) 不受影响

            binder.Revert(target, "material", slot2Applicator);
            Assert.Null(target.Second);
            await manager.ShutdownAsync();
        }

        private sealed class SecondSlotApplicator : IResourceApplicator<RecordingTarget, FakeAsset>
        {
            public void Replace(RecordingTarget target, FakeAsset value) => target.Second = value.Name;
            public void Revert(RecordingTarget target) => target.Second = null;
        }

        // ---- R34: ResourceScope 逆序清理与异常聚合 ----
        [Fact]
        public void ResourceScope_ReverseOrder_AndExceptionAggregation()
        {
            var scope = new ResourceScope();
            var order = new System.Collections.Generic.List<int>();
            scope.Own(() => order.Add(1));
            scope.Own(() => order.Add(2));
            scope.Own(() =>
            {
                order.Add(3);
                throw new InvalidOperationException("cleanup 3 failed");
            });

            var ex = Assert.Throws<AggregateException>(() => scope.Dispose());
            Assert.Equal(new[] { 3, 2, 1 }, order); // 逆序执行
            Assert.Single(ex.InnerExceptions); // 单项失败不跳过其余项
        }
    }

}
