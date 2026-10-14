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
            manager.RegisterLoader(ResourceSource.Local, loader);
            manager.RegisterFactory("fakeFactory", factory);
            return manager;
        }

        private static Task<InstanceLease<FakeInstance>> RentAsync(ResourceManager manager, string address = "enemy")
        {
            return manager.RentAsync<FakeInstance>(address, source: ResourceSource.Local, factory: "fakeFactory");
        }

        // ---- R07: 关闭期间创建失败(Incomplete) 不再绕过清理语义 ----
        [Fact]
        public async Task R07_CreateFailsIncompleteDuringClear_PoolCloseReportsFault()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            var createGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var remainder = new object();
            var gatedFactory = new GatedCreateFactory(factory, createGate)
            {
                FailWith = new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate, CleanupStatus.Incomplete),
                    new[] { remainder }),
            };
            manager.RegisterFactory("gatedFactory", gatedFactory);
            await manager.InitializeAsync();

            var rentTask = manager.RentAsync<FakeInstance>("enemy", source: ResourceSource.Local, factory: "gatedFactory");
            await gatedFactory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var clearTask = manager.ClearPoolAsync("enemy", source: ResourceSource.Local, factory: "gatedFactory");

            createGate.TrySetResult(true); // 关闭期间创建以 Incomplete 失败

            var clearEx = await Assert.ThrowsAnyAsync<ResourceLoadException>(() => clearTask);
            Assert.Equal(CleanupStatus.Incomplete, clearEx.Error.Cleanup);
            Assert.Contains(remainder, Assert.Single(manager.GetSnapshot().DirtyResources).RetainedObjects);
            Assert.Empty(manager.GetSnapshot().PoolRows); // 残留移交脏集合，关闭流程仍可结束。
            var rentEx = await Assert.ThrowsAsync<ResourceLoadException>(() => rentTask);
            Assert.True(
                rentEx.Error.DiagnosticCode == DiagnosticCodes.InstanceCreateFailed
                || rentEx.Error.DiagnosticCode == DiagnosticCodes.InstancePoolClosed,
                $"等待者应被结算而非悬挂: {rentEx.Error.DiagnosticCode}");
            manager.Dispose();
        }

        private sealed class GatedCreateFactory : IInstanceFactory
        {
            private readonly FakeInstanceFactory _inner;
            private readonly TaskCompletionSource<bool> _gate;
            public Exception? FailWith;
            public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                Started.TrySetResult(true);
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
                clearObserved = manager.ClearPoolAsync("enemy", source: ResourceSource.Local, factory: "reentrant");
                return true;
            }, returnGate);
            manager.RegisterFactory("reentrant", reentrant);
            await manager.InitializeAsync();

            var lease = await manager.RentAsync<FakeInstance>("enemy", source: ResourceSource.Local, factory: "reentrant");
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
            manager.Dispose();
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
            await manager.InitializeAsync();
            factory.ThrowOnCanCreate = new InvalidOperationException("CanCreate broken");

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => RentAsync(manager));
            Assert.Equal(DiagnosticCodes.InstanceUnsupported, ex.Error.DiagnosticCode);
            Assert.NotNull(ex.Error.Cause);

            // 原型引用已归还，桶已移除，关闭可正常完成
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
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

        // ---- R25: 归还只进入空闲，资源卸载时机由业务决定 ----
        [Fact]
        public async Task R25_ReturnKeepsCachedResource_UntilBusinessUnload()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var reference = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            reference.Dispose();
            Assert.Equal(0, loader.ReleaseCount);
            Assert.Equal(ResourceState.Idle, Assert.Single(manager.GetSnapshot().ResourceRows).State);
            var result = await manager.UnloadUnusedAsync();
            Assert.Equal(1, result.ReleasedCount);
            Assert.Equal(1, loader.ReleaseCount);
            manager.Dispose();
        }

        // ---- R26: IsAlive 查询失败不当作已销毁，实例按销毁协议清理 ----
        [Fact]
        public async Task R26_IsAliveThrows_AfterCreate_InstanceDestroyedNotDropped()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var aliveFactory = new FlakyAliveFactory(factory, gate);
            manager.RegisterFactory("flaky", aliveFactory);
            await manager.InitializeAsync();

            var rentTask = manager.RentAsync<FakeInstance>("enemy", source: ResourceSource.Local, factory: "flaky");
            aliveFactory.FailNextIsAlive = true; // 创建完成后首次存活查询失败
            gate.TrySetResult(true);

            var captured = await V2Test.Capture(rentTask);
            var ex = captured as ResourceLoadException;
            Assert.True(ex != null,
                $"应抛 RLE: got={captured?.GetType().Name}, aliveCalls={aliveFactory.AliveCalls}, " +
                $"flagNow={aliveFactory.FailNextIsAlive}, destroy={factory.DestroyCount}, create={factory.CreateCount}");
            Assert.Equal(DiagnosticCodes.InstanceCreateFailed, ex!.Error.DiagnosticCode);
            Assert.Equal(1, factory.DestroyCount); // 保留记录并按销毁协议清理，不静默丢弃

            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
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

        // ---- R27: 旧代池销毁失败仅隔离旧实例，不影响同键新池与租用 ----
        [Fact]
        public async Task R27_OldDestroyFailure_DoesNotBlockNewGeneration()
        {
            var manager = NewRentedManager(out var loader, out var factory);
            await manager.InitializeAsync();

            var oldLease = await RentAsync(manager);
            var oldInstance = oldLease.Value;
            var clearTask = manager.ClearPoolAsync("enemy", source: ResourceSource.Local, factory: "fakeFactory");
            var newLease = await RentAsync(manager);
            Assert.Equal(2, manager.GetSnapshot().PoolRows.Count);

            factory.ThrowOnDestroy = new InvalidOperationException("destroy broken");
            oldLease.Dispose();
            var clearError = await Assert.ThrowsAsync<ResourceLoadException>(() => clearTask);
            Assert.Equal(DiagnosticCodes.InstanceDestroyFailed, clearError.Error.DiagnosticCode);
            Assert.Contains(oldInstance, Assert.Single(manager.GetSnapshot().DirtyResources).RetainedObjects);
            Assert.True(newLease.IsValid);

            var third = await RentAsync(manager);
            Assert.NotSame(oldInstance, third.Value);
            Assert.Single(manager.GetSnapshot().PoolRows);
            factory.ThrowOnDestroy = null;
            third.Dispose();
            newLease.Dispose();
            await manager.ClearPoolAsync("enemy", source: ResourceSource.Local, factory: "fakeFactory");
            Assert.False(oldInstance.Destroyed);
            Assert.Equal(2, factory.DestroyCount);
            manager.Dispose();
        }

        // ---- R29: 省略加载器策略时默认四路并发 ----
        [Fact]
        public async Task R29_LoaderPolicyDefaultsToFourConcurrentLoads()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader(); // 常规注册：不带策略
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var tasks = new Task<ResourceRef<FakeAsset>>[8];
            for (var i = 0; i < 8; i++)
            {
                tasks[i] = manager.LoadAsync<FakeAsset>("res-" + i, source: ResourceSource.Local);
            }
            // 消费者独立执行；先形成真实四路在途工作，再放行后端验证并发上限。
            await V2Test.WaitUntilAsync(() => Volatile.Read(ref loader.ActiveCount) == 4);
            Assert.Equal(4, loader.LoadCount);
            Assert.All(tasks, task => Assert.False(task.IsCompleted));
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
            manager.Dispose();
        }

        // ---- R30: 注入的用户错误映射器在交付边界生效 ----
        [Fact]
        public async Task R30_CustomErrorMapper_AppliedAtDelivery()
        {
            var mapper = new ForceStorageFullMapper();
            var manager = new ResourceManager(ImmediateExecutionContext.Instance,
                new ResourceSystemOptions(), mapper, null);
            await manager.InitializeAsync();

            var ex = await Assert.ThrowsAsync<ResourceLoadException>(() => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Remote));
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
            manager.RegisterLoader(ResourceSource.Local, new FakeLoader());
            manager.RegisterFactory("f", new FakeInstanceFactory());
            Assert.Throws<InvalidOperationException>(() => manager.RegisterLoader(ResourceSource.Local, new FakeLoader()));
            Assert.Throws<InvalidOperationException>(() => manager.RegisterFactory("f", new FakeInstanceFactory()));

            manager.ReplaceLoader(ResourceSource.Local, new FakeLoader { Factory = _ => new FakeAsset("replaced") });
            await manager.InitializeAsync();
            var reference = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            Assert.Equal("replaced", reference.Value.Name);
            reference.Dispose();
            manager.Dispose();
        }

        // ---- R32: UnloadUnusedAsync 取消只中断等待，底层卸载继续 ----
        [Fact]
        public async Task R32_UnloadUnused_CancelReturnsImmediately_BackendContinues()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new SlowReleaseLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var reference = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            reference.Dispose(); // 进入空闲缓存 (尚未卸载)

            using var cts = new CancellationTokenSource(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.UnloadUnusedAsync(cancellationToken: cts.Token));

            loader.ReleaseGate.TrySetResult(true); // 底层卸载继续并只执行一次
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
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
                return new LoadedAsset(value, null, async () =>
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
            var manager = V2Test.NewManager(out _, out _);
            var loader = new SlowReleaseLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var first = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            var firstValue = first.Value;
            first.Dispose();
            var unload = manager.UnloadUnusedAsync(); // 业务发起卸载，后端被门控挂起

            var secondTask = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local); // 旧卸载未结束：等待同键屏障
            loader.ReleaseGate.TrySetResult(true);
            var second = await secondTask;
            await unload;
            Assert.NotSame(firstValue, second.Value);
            Assert.Equal(2, loader.LoadCountOf); // 屏障结束后新记录是新的一次加载

            second.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 2);
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
            public Action? DuringReplace;
            public Action? DuringRevert;

            public void Replace(RecordingTarget target, FakeAsset value)
            {
                var old = target.Current;
                try
                {
                    if (ThrowOnReplace != null)
                    {
                        throw ThrowOnReplace; // 模拟属性设置失败
                    }
                    DuringReplace?.Invoke();
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
                DuringRevert?.Invoke();
                target.Current = null;
            }
        }

        [Fact]
        public async Task E09_LatestBindingWins_SupersededNotApplied()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var binder = new ResourceBinder(manager);
            var target = new RecordingTarget();
            var applicator = new RecordingApplicator();

            var applyA = binder.ApplyAsync(target, "sprite", "a", applicator, source: ResourceSource.Local);
            var applyB = binder.ApplyAsync(target, "sprite", "b", applicator, source: ResourceSource.Local);
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
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var binder = new ResourceBinder(manager);
            var target = new RecordingTarget();
            var applicator = new RecordingApplicator();

            await binder.ApplyAsync(target, "sprite", "a", applicator, source: ResourceSource.Local);
            Assert.Equal("a", target.Current);

            applicator.ThrowOnReplace = new InvalidOperationException("apply failed");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => binder.ApplyAsync(target, "sprite", "b", applicator, source: ResourceSource.Local));
            Assert.Equal("a", target.Current); // 旧展示保留

            applicator.ThrowOnReplace = null;
            await binder.ApplyAsync(target, "sprite", "b", applicator, source: ResourceSource.Local);
            Assert.Equal("b", target.Current);
            binder.Revert(target, "sprite", applicator);
            manager.Dispose();
        }

        [Fact]
        public async Task E11_UnbindOrder_RevertClearsBeforeDispose()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader { Factory = a => new FakeAsset(a) };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var binder = new ResourceBinder(manager);
            var target = new RecordingTarget();
            var applicator = new RecordingApplicator();

            await binder.ApplyAsync(target, "sprite", "a", applicator, source: ResourceSource.Local);
            var snapshot = manager.GetSnapshot();
            Assert.Equal(1, snapshot.ResourceRows[0].HoldCount); // slot 持有

            binder.Revert(target, "sprite", applicator);
            Assert.Null(target.Current); // 先清属性
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount); // 底层释放发生在解绑之后
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BindingCallback_ReentrantAsyncApplyIsRejected_WithoutLosingSlot(bool duringRevert)
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var binder = new ResourceBinder(manager);
            var target = new RecordingTarget();
            var applicator = new RecordingApplicator();
            loader.ReleaseGate("old");
            await binder.ApplyAsync(target, "sprite", "old", applicator, ResourceSource.Local);

            Task<BindingResult>? nested = null;
            Action reenter = () =>
            {
                applicator.DuringReplace = null;
                applicator.DuringRevert = null;
                // 新地址需要真正异步加载；不能只验证缓存命中的同步交付门闩。
                nested = binder.ApplyAsync(target, "sprite", "nested", applicator, ResourceSource.Local);
            };
            if (duringRevert)
            {
                applicator.DuringRevert = reenter;
                binder.Revert(target, "sprite", applicator);
            }
            else
            {
                applicator.DuringReplace = reenter;
                loader.ReleaseGate("replacement");
                Assert.Equal(BindingResult.Applied, await binder.ApplyAsync(
                    target, "sprite", "replacement", applicator, ResourceSource.Local));
                Assert.Equal("replacement", target.Current);
            }

            Assert.NotNull(nested);
            loader.ReleaseGate("nested");
            await Assert.ThrowsAsync<InvalidOperationException>(() => nested!.WaitAsync(TimeSpan.FromSeconds(5)));
            binder.Revert(target, "sprite", applicator);
            Assert.Null(target.Current);
            var unloaded = await manager.UnloadUnusedAsync();
            Assert.Equal(0, unloaded.SkippedInUseCount);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
            manager.Dispose();
        }

        [Fact]
        public async Task CancelApply_SupersedesPendingRequestAndPreservesCurrentBinding()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new GatedLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var binder = new ResourceBinder(manager);
            var target = new RecordingTarget();
            var applicator = new RecordingApplicator();
            loader.ReleaseGate("old");
            await binder.ApplyAsync(target, "sprite", "old", applicator, ResourceSource.Local);

            var pending = binder.ApplyAsync(target, "sprite", "replacement", applicator, ResourceSource.Local);
            await V2Test.WaitUntilAsync(() => loader.LoadCount == 2);
            binder.CancelApply(target, "sprite");
            Assert.Equal(BindingResult.Superseded, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await V2Test.WaitUntilAsync(() => manager.GetSnapshot().ResourceRows.Count == 1);
            Assert.Equal("old", target.Current);
            Assert.Equal(1, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);

            binder.Revert(target, "sprite", applicator);
            Assert.Null(target.Current);
            var unloaded = await manager.UnloadUnusedAsync();
            Assert.Equal(1, unloaded.ReleasedCount);
            Assert.Equal(0, unloaded.SkippedInUseCount);
            manager.Dispose();
        }

        [Fact]
        public async Task BindingCancellationCallbackThrows_NextApplyCanStillBindAndRevert()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new ThrowingResolveCancellationLoader();
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var binder = new ResourceBinder(manager);
            var target = new RecordingTarget();
            var applicator = new RecordingApplicator();

            var old = binder.ApplyAsync(target, "sprite", "pending-resolve", applicator, ResourceSource.Local);
            await loader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await Assert.ThrowsAsync<AggregateException>(() => binder.ApplyAsync(
                target, "sprite", "interrupted", applicator, ResourceSource.Local));
            Assert.Contains(error.Flatten().InnerExceptions,
                exception => exception.Message == "resolve cancellation failed");
            Assert.Equal(BindingResult.Superseded, await old.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(BindingResult.Applied, await binder.ApplyAsync(
                target, "sprite", "good", applicator, ResourceSource.Local).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("good", target.Current);
            binder.Revert(target, "sprite", applicator);
            Assert.Null(target.Current);
            var unloaded = await manager.UnloadUnusedAsync();
            Assert.Equal(1, unloaded.ReleasedCount);
            Assert.Equal(0, unloaded.SkippedInUseCount);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
            manager.Dispose();
        }

        private sealed class ThrowingResolveCancellationLoader : IResourceLoader
        {
            private readonly FakeLoader _inner = new FakeLoader();
            public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken token)
            {
                if (request.Address != "pending-resolve")
                    return await _inner.ResolveAsync(request, token);
                var pending = new TaskCompletionSource<ResolvedResource>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() =>
                {
                    pending.TrySetCanceled(token);
                    throw new InvalidOperationException("resolve cancellation failed");
                }))
                {
                    Started.TrySetResult(true);
                    return await pending.Task.ConfigureAwait(false);
                }
            }

            public Task<LoadedAsset> LoadAsync(ResolvedResource resource, IProgress<ResourceProgress> progress,
                CancellationToken token) => _inner.LoadAsync(resource, progress, token);
        }

        [Fact]
        public async Task E13_IndependentSlots_SameTarget()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FakeLoader { Factory = a => new FakeAsset(a) };
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();
            var binder = new ResourceBinder(manager);
            var target = new RecordingTarget();
            var applicator = new RecordingApplicator();
            var slot2Applicator = new SecondSlotApplicator();

            await binder.ApplyAsync(target, "sprite", "a", applicator, source: ResourceSource.Local);
            await binder.ApplyAsync(target, "material", "b", slot2Applicator, source: ResourceSource.Local);

            binder.Revert(target, "sprite", applicator);
            Assert.Null(target.Current);            // sprite 槽被解除
            Assert.Equal("b", target.Second);       // 另一 slot (material) 不受影响

            binder.Revert(target, "material", slot2Applicator);
            Assert.Null(target.Second);
            manager.Dispose();
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
