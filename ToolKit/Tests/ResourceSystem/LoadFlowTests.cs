#nullable disable
using ToolKit.Tools.Common;
using RS = ToolKit.Tools.Common.ResourceSystem;

namespace ToolKit.Tests.ResourceSystem;

public class LoadFlowTests
{
    private static readonly TimeSpan Timeout = TestClock.Timeout;

    [Fact]
    public async Task LoadBeforeInitializeIsRejected_InitializeIsIdempotentAndRetriable()
    {
        var loader = new FakeLoader();
        var attempts = 0;
        var firstRound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = TestSystem.Options(loader);
        options.Initializers.Add(_ => ++attempts == 1 ? firstRound.Task : Task.CompletedTask);
        var system = new RS(options);

        var early = await Assert.ThrowsAsync<ResourceException>(() => system.LoadAsync<string>(ResourceSource.Local, "a"));
        Assert.Equal(DiagnosticCodes.LifecycleManagerDisposed, early.ResourceError.DiagnosticCode);

        var first = system.InitializeAsync();
        var second = system.InitializeAsync();
        Assert.Same(first, second);                                   // 同轮幂等：观察同一结果
        firstRound.SetException(new IOException("首轮失败"));
        await Assert.ThrowsAsync<IOException>(() => first);
        await Assert.ThrowsAsync<IOException>(() => second);
        Assert.False(system.IsInitialized);

        await system.InitializeAsync();                               // 失败后重试
        Assert.True(system.IsInitialized);
        Assert.Equal(2, attempts);
        using var reference = await system.LoadAsync<string>(ResourceSource.Local, "a");
        Assert.Equal("product:a", reference.Value);
    }

    [Fact]
    public async Task ConcurrentSameKeyRequestsShareOneLoad_CancelingOneDoesNotAffectOthers()
    {
        var loader = new FakeLoader { AutoComplete = false };
        await using var system = await TestSystem.CreateAsync(loader);
        using var cancelA = new CancellationTokenSource();

        var a = system.LoadAsync<string>(ResourceSource.Local, "x", token: cancelA.Token);
        var b = system.LoadAsync<string>(ResourceSource.Local, "x");
        var pending = await loader.WaitForPendingAsync(0);
        await Task.Delay(50);
        Assert.Equal(1, loader.LoadCount);                            // 同身份只进入一次实际加载

        cancelA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.WaitAsync(Timeout));
        Assert.False(pending.RoundToken.IsCancellationRequested);     // 还有 B 在等：本轮继续

        pending.Complete();
        using var refB = await b.WaitAsync(Timeout);
        Assert.Equal("product:x", refB.Value);
        Assert.Equal(1, loader.LoadCount);
    }

    [Fact]
    public async Task BackendFailureSettlesExactlyOneWaiter_OthersContinueIntoNextRound()
    {
        var loader = new FakeLoader { AutoComplete = false };
        await using var system = await TestSystem.CreateAsync(loader);

        var tasks = Enumerable.Range(0, 3).Select(_ => system.LoadAsync<string>(ResourceSource.Local, "x")).ToArray();
        var round1 = await loader.WaitForPendingAsync(0);
        await Task.Delay(50);
        round1.Fail(new IOException("磁盘错误"));

        var round2 = await loader.WaitForPendingAsync(1);            // 其余等待者自动进入下一轮
        round2.Complete();
        try { await Task.WhenAll(tasks).WaitAsync(Timeout); } catch { /* 预期有一名承担者失败 */ }

        var failed = tasks.Where(t => t.IsFaulted).ToArray();
        var succeeded = tasks.Where(t => t.Status == TaskStatus.RanToCompletion).ToArray();
        _ = Assert.Single(failed);                                    // 只结算一个承担者
        Assert.Equal(2, succeeded.Length);
        var error = Assert.IsType<ResourceException>(failed[0].Exception!.InnerException);
        Assert.IsType<IOException>(error.ResourceError.Cause);
        Assert.Equal(2, loader.LoadCount);
        foreach (var task in succeeded) (await task).Dispose();
    }

    [Fact]
    public async Task SingleWaiterFailurePropagates_AndNextRequestStartsNewRound()
    {
        var loader = new FakeLoader { AutoComplete = false };
        await using var system = await TestSystem.CreateAsync(loader);

        var a = system.LoadAsync<string>(ResourceSource.Local, "x");
        (await loader.WaitForPendingAsync(0)).Fail(new IOException("x"));
        var error = await Assert.ThrowsAsync<ResourceException>(() => a.WaitAsync(Timeout));
        Assert.Equal(DiagnosticCodes.AssetLoadFailed, error.ResourceError.DiagnosticCode);

        var b = system.LoadAsync<string>(ResourceSource.Local, "x");
        (await loader.WaitForPendingAsync(1)).Complete();
        using var refB = await b.WaitAsync(Timeout);
        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public async Task WhenAllWaitersLeave_RoundIsCanceled_AndLateProductIsReleased()
    {
        var loader = new FakeLoader { AutoComplete = false, ObserveRoundCancel = false };
        await using var system = await TestSystem.CreateAsync(loader);
        using var cancel = new CancellationTokenSource();

        var a = system.LoadAsync<string>(ResourceSource.Local, "x", token: cancel.Token);
        var pending = await loader.WaitForPendingAsync(0);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.WaitAsync(Timeout));
        await TestClock.WaitUntilAsync(() => pending.RoundToken.IsCancellationRequested);

        var late = pending.Complete();                                // 后端没停下来，迟到的产物
        await TestClock.WaitUntilAsync(() => late.Released == 1);     // 由本轮清理，不进缓存
        Assert.Empty(system.Snapshot().Entries);
    }

    [Fact]
    public async Task NewRequestAfterAbandonedRoundStartsFreshRound()
    {
        var loader = new FakeLoader { AutoComplete = false };
        await using var system = await TestSystem.CreateAsync(loader);
        using var cancel = new CancellationTokenSource();

        var a = system.LoadAsync<string>(ResourceSource.Local, "x", token: cancel.Token);
        var first = await loader.WaitForPendingAsync(0);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.WaitAsync(Timeout));

        var b = system.LoadAsync<string>(ResourceSource.Local, "x");
        first.CancelByRound();                                        // 旧轮真正退出后新轮才开始
        var second = await loader.WaitForPendingAsync(1);
        second.Complete();
        using var refB = await b.WaitAsync(Timeout);
        Assert.Equal("product:x", refB.Value);
    }

    [Fact]
    public async Task TimeoutEndsTheRequestWithRequestTimeout()
    {
        var loader = new FakeLoader { AutoComplete = false };
        await using var system = await TestSystem.CreateAsync(loader, new LoaderPolicy(4, TimeSpan.FromMilliseconds(100)));

        var error = await Assert.ThrowsAsync<ResourceException>(
            () => system.LoadAsync<string>(ResourceSource.Local, "slow").WaitAsync(Timeout));
        Assert.Equal(DiagnosticCodes.RequestTimeout, error.ResourceError.DiagnosticCode);
        var pending = loader.Pending(0);
        await TestClock.WaitUntilAsync(() => pending.RoundToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ConverterProducesRequestedType_AndIntermediateProductIsReleased()
    {
        var loader = new FakeLoader { Produce = _ => new byte[] { 104, 105 } };
        var converter = new FuncConverter(bytes => System.Text.Encoding.ASCII.GetString((byte[])bytes));
        await using var system = await TestSystem.CreateAsync(loader, configure: o => o.AddConverter<string>(converter));

        using var text = await system.LoadAsync<string>(ResourceSource.Local, "x");
        Assert.Equal("hi", text.Value);
        Assert.Equal(1, loader.Products[0].Released);                 // 中间产物用完即释放

        var mismatch = await Assert.ThrowsAsync<ResourceException>(() => system.LoadAsync<int[]>(ResourceSource.Local, "x"));
        Assert.Equal(DiagnosticCodes.AssetTypeMismatch, mismatch.ResourceError.DiagnosticCode);
        Assert.Equal(1, loader.Products[1].Released);

        var broken = new FuncConverter(_ => throw new FormatException("坏数据"));
        await using var system2 = await TestSystem.CreateAsync(new FakeLoader { Produce = _ => new byte[1] },
            configure: o => o.AddConverter<string>(broken));
        var failure = await Assert.ThrowsAsync<ResourceException>(() => system2.LoadAsync<string>(ResourceSource.Local, "x"));
        Assert.Equal(DiagnosticCodes.AssetConversionFailed, failure.ResourceError.DiagnosticCode);
        Assert.IsType<FormatException>(failure.ResourceError.Cause);
    }

    [Fact]
    public async Task ConcurrencyLimitBoundsActualLoads_NotWaiters()
    {
        var loader = new FakeLoader { AutoComplete = false };
        await using var system = await TestSystem.CreateAsync(loader, new LoaderPolicy(1, null));

        var a = system.LoadAsync<string>(ResourceSource.Local, "a");
        var a2 = system.LoadAsync<string>(ResourceSource.Local, "a");
        var b = system.LoadAsync<string>(ResourceSource.Local, "b");
        var first = await loader.WaitForPendingAsync(0);
        await Task.Delay(100);
        Assert.Equal(1, loader.PendingCount);                         // 名额 1：第二个键排队

        first.Complete();
        (await loader.WaitForPendingAsync(1)).Complete();
        using var refA = await a.WaitAsync(Timeout);
        using var refA2 = await a2.WaitAsync(Timeout);
        using var refB = await b.WaitAsync(Timeout);
        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public async Task ProgressReachesEveryWaiter_AndListenerFailureIsIsolated()
    {
        var loader = new FakeLoader { AutoComplete = false };
        await using var system = await TestSystem.CreateAsync(loader);
        var seenA = new List<double?>();
        var seenB = new List<double?>();
        var a = system.LoadAsync<string>(ResourceSource.Local, "x", progress: new SyncProgress(p => seenA.Add(p.Fraction)));
        var b = system.LoadAsync<string>(ResourceSource.Local, "x", progress: new SyncProgress(p => { seenB.Add(p.Fraction); throw new Exception("监听器异常"); }));
        var pending = await loader.WaitForPendingAsync(0);
        await Task.Delay(50);

        pending.Report(0.5);
        pending.Complete();
        using var refA = await a.WaitAsync(Timeout);
        using var refB = await b.WaitAsync(Timeout);
        Assert.Equal(new double?[] { 0.5 }, seenA);
        Assert.Equal(new double?[] { 0.5 }, seenB);
    }

    [Fact]
    public async Task UnregisteredSourceIsRejected_ParametersParticipateInIdentity()
    {
        var loader = new FakeLoader();
        await using var system = await TestSystem.CreateAsync(loader);
        var error = await Assert.ThrowsAsync<ResourceException>(() => system.LoadAsync<string>(ResourceSource.Remote, "x"));
        Assert.Equal(DiagnosticCodes.LoaderNotRegistered, error.ResourceError.DiagnosticCode);

        using var plain = await system.LoadAsync<string>(ResourceSource.Local, "x");
        using var variant = await system.LoadAsync<string>(ResourceSource.Local, "x", parameters: "hd");
        using var variantAgain = await system.LoadAsync<string>(ResourceSource.Local, "x", parameters: "hd");
        Assert.Equal(2, loader.LoadCount);
        Assert.NotEqual(plain.Key, variant.Key);
        Assert.Equal(variant.Key, variantAgain.Key);
    }

    [Fact]
    public async Task LoaderCanBeRegisteredAfterInitialize_AndSystemMayStartWithNone()
    {
        var options = new ResourceSystemOptions();                     // 一个加载器都没有也能起
        await using var system = new RS(options);
        await system.InitializeAsync();
        var missing = await Assert.ThrowsAsync<ResourceException>(() => system.LoadAsync<string>(ResourceSource.AssetBundle, "ab/x"));
        Assert.Equal(DiagnosticCodes.LoaderNotRegistered, missing.ResourceError.DiagnosticCode);

        var bundles = new FakeLoader();
        system.RegisterLoader(ResourceSource.AssetBundle, bundles, new LoaderPolicy(2, null));   // 包系统就绪后再接入
        using var asset = await system.LoadAsync<string>(ResourceSource.AssetBundle, "ab/x");
        Assert.Equal("product:ab/x", asset.Value);
        Assert.Equal(1, bundles.LoadCount);
    }

    [Fact]
    public async Task ReplacingLoader_LetsInFlightLoadFinish_RoutesNewRequestsToNewLoader_StopsBothOnDispose()
    {
        var old = new FakeLoader { AutoComplete = false };
        var system = await TestSystem.CreateAsync(old);
        var inFlight = system.LoadAsync<string>(ResourceSource.Local, "x");
        var pending = await old.WaitForPendingAsync(0);

        var fresh = new FakeLoader { AutoComplete = false, Produce = key => "fresh:" + key.Address };
        system.RegisterLoader(ResourceSource.Local, fresh);
        var afterSwap = system.LoadAsync<string>(ResourceSource.Local, "y");
        (await fresh.WaitForPendingAsync(0)).Complete();
        using var refY = await afterSwap.WaitAsync(Timeout);
        Assert.Equal("fresh:y", refY.Value);
        Assert.False(pending.RoundToken.IsCancellationRequested);     // 旧加载器在途的一轮照常做完

        pending.Complete();
        using var refX = await inFlight.WaitAsync(Timeout);
        Assert.Equal("product:x", refX.Value);
        using var hit = await system.LoadAsync<string>(ResourceSource.Local, "x");
        Assert.Equal(1, fresh.LoadCount);                              // 旧加载器的产物仍在缓存里

        var stuck = system.LoadAsync<string>(ResourceSource.Local, "z");
        await fresh.WaitForPendingAsync(1);
        await system.DisposeAsync().AsTask().WaitAsync(Timeout);       // 退出时新旧管理器都被停止，不悬挂
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stuck.WaitAsync(Timeout));
    }

    [Fact]
    public async Task BearerCancelsEarly_RoundFailureIsNotTransferredToRemainingWaiter()
    {
        var loader = new FakeLoader { AutoComplete = false };
        await using var system = await TestSystem.CreateAsync(loader);
        using var cancelA = new CancellationTokenSource();

        var a = system.LoadAsync<string>(ResourceSource.Local, "x", token: cancelA.Token);
        var b = system.LoadAsync<string>(ResourceSource.Local, "x");
        var round1 = await loader.WaitForPendingAsync(0);

        cancelA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.WaitAsync(Timeout));

        // 承担者 A 已提前取消；本轮失败不得转嫁给 B，B 应自动进入下一轮 (Q01)
        round1.Fail(new IOException("首轮失败"));
        var round2 = await loader.WaitForPendingAsync(1).WaitAsync(Timeout);
        round2.Complete();

        using var refB = await b.WaitAsync(Timeout);
        Assert.Equal("product:x", refB.Value);
        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public async Task ConcurrentUnloadDuringDelivery_NeverEvictsNewlyLoadedProductOrReloads()
    {
        var loader = new FakeLoader { AutoComplete = false };
        await using var system = await TestSystem.CreateAsync(loader, new LoaderPolicy(8, null));
        using var stopUnload = new CancellationTokenSource();

        var unloader = Task.Run(async () =>
        {
            while (!stopUnload.IsCancellationRequested)
            {
                await system.UnloadAsync();
                Thread.Yield();
            }
        });

        const int keys = 40;
        var refs = new List<ResourceRef<string>>(keys);
        try
        {
            for (var i = 0; i < keys; i++)
            {
                var load = system.LoadAsync<string>(ResourceSource.Local, "k" + i);
                var pending = await loader.WaitForPendingAsync(i);
                pending.Complete();
                refs.Add(await load.WaitAsync(Timeout));
            }
            Assert.Equal(keys, loader.LoadCount);                     // 交付与计持有原子完成，不会被并发卸载抢走重算
        }
        finally
        {
            stopUnload.Cancel();
            await unloader;
            foreach (var r in refs) r.Dispose();
        }
    }

    [Fact]
    public async Task LoaderIsInvokedInsideExecutionContext()
    {
        var context = new TrackingExecutionContext();
        var observedInContext = false;
        var loader = new FakeLoader
        {
            Produce = _ =>
            {
                observedInContext = context.IsCurrent;
                return "ok";
            }
        };
        await using var system = await TestSystem.CreateAsync(loader, configure: o => o.ExecutionContext = context);

        using var reference = await system.LoadAsync<string>(ResourceSource.Local, "ctx");
        Assert.Equal("ok", reference.Value);
        Assert.True(observedInContext);
    }

    [Fact]
    public async Task ReplacingLoader_SameKeyJoinsInFlight_AndRetryUsesNewLoader()
    {
        var old = new FakeLoader { AutoComplete = false };
        var fresh = new FakeLoader { AutoComplete = false, Produce = key => "fresh:" + key.Address };
        await using var system = await TestSystem.CreateAsync(old);

        var first = system.LoadAsync<string>(ResourceSource.Local, "x");
        var oldRound = await old.WaitForPendingAsync(0);

        system.RegisterLoader(ResourceSource.Local, fresh);
        var second = system.LoadAsync<string>(ResourceSource.Local, "x"); // 同键请求直接并入在途操作
        Assert.Equal(0, fresh.LoadCount);

        oldRound.Fail(new IOException("旧加载器失败"));                  // 承担者 first 失败，second 接续到新加载器
        await Assert.ThrowsAsync<ResourceException>(() => first.WaitAsync(Timeout));

        var freshRound = await fresh.WaitForPendingAsync(0).WaitAsync(Timeout);
        freshRound.Complete();
        using var refSecond = await second.WaitAsync(Timeout);
        Assert.Equal("fresh:x", refSecond.Value);
    }

    [Fact]
    public async Task ReplayCanCancelItselfAndReenterWithoutLosingWaiterCount()
    {
        var loader = new FakeLoader { AutoComplete = false };
        await using var system = await TestSystem.CreateAsync(loader);
        using var cancelFirst = new CancellationTokenSource();
        using var cancelReplay = new CancellationTokenSource();
        using var cancelJoined = new CancellationTokenSource();

        var first = system.LoadAsync<string>(ResourceSource.Local, "x", token: cancelFirst.Token);
        var pending = await loader.WaitForPendingAsync(0);
        pending.Report(0.4);

        Task<ResourceRef<string>> reentered = null;
        var replay = system.LoadAsync<string>(
            ResourceSource.Local,
            "x",
            progress: new SyncProgress(_ =>
            {
                cancelReplay.Cancel();
                reentered = system.LoadAsync<string>(ResourceSource.Local, "x", token: cancelJoined.Token);
            }),
            token: cancelReplay.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.WaitAsync(Timeout));
        Assert.NotNull(reentered);
        Assert.False(pending.RoundToken.IsCancellationRequested);

        cancelFirst.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Timeout));
        Assert.False(pending.RoundToken.IsCancellationRequested);         // 重入加入的请求仍在等

        cancelJoined.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reentered.WaitAsync(Timeout));
        await TestClock.WaitUntilAsync(() => pending.RoundToken.IsCancellationRequested);
    }

    [Fact]
    public async Task InvalidProductAndReleaseFailureAreTrackedAsResidue_AndRetriedOnUnload()
    {
        var releaseError = new IOException("释放失败");
        var broken = new FakeProduct(null) { ReleaseError = releaseError };
        var loader = new CustomProductLoader(_ => broken);
        await using var system = new RS(new ResourceSystemOptions().AddLoader(ResourceSource.Local, loader));
        await system.InitializeAsync();

        var err = await Assert.ThrowsAsync<ResourceException>(() => system.LoadAsync<string>(ResourceSource.Local, "bad"));
        Assert.Equal(DiagnosticCodes.LoaderInvalidResult, err.ResourceError.DiagnosticCode);
        Assert.Equal(1, broken.Released);
        var residue = Assert.Single(system.Snapshot().Residues);
        Assert.Same(releaseError, residue.Error);

        broken.ReleaseError = null;
        var unloaded = await system.UnloadAsync();
        Assert.Single(unloaded.Released);
        Assert.Empty(unloaded.Failed);
        Assert.Empty(system.Snapshot().Residues);
        Assert.Equal(2, broken.Released);
    }

    [Fact]
    public async Task RetainCreatesIndependentHold_AndDisposedRefRejectsAccessAndRetain()
    {
        var loader = new FakeLoader();
        await using var system = await TestSystem.CreateAsync(loader);

        var first = await system.LoadAsync<string>(ResourceSource.Local, "shared");
        var retained = first.Retain();
        Assert.Equal(2, Assert.Single(system.Snapshot().Entries).Holds);

        first.Dispose();
        Assert.False(first.IsValid);
        Assert.Throws<ResourceException>(() => _ = first.Value);
        Assert.Throws<ResourceException>(() => first.Retain());
        Assert.Equal("product:shared", retained.Value);

        var inUse = await system.UnloadAsync();
        Assert.Single(inUse.SkippedInUse);

        retained.Dispose();
        var released = await system.UnloadAsync();
        Assert.Single(released.Released);
        Assert.Equal(1, loader.Products[0].Released);
    }

    [Fact]
    public async Task DisposeRunsShutdownActionsInReverse_AndAggregatesFailures()
    {
        var order = new List<int>();
        var options = TestSystem.Options(new FakeLoader());
        options.ShutdownActions.Add(() => { order.Add(1); return Task.CompletedTask; });
        options.ShutdownActions.Add(() => { order.Add(2); throw new InvalidOperationException("关闭失败"); });
        options.ShutdownActions.Add(() => { order.Add(3); return Task.CompletedTask; });
        var system = new RS(options);
        await system.InitializeAsync();

        var agg = await Assert.ThrowsAsync<AggregateException>(() => system.DisposeAsync().AsTask());
        Assert.Single(agg.InnerExceptions);
        Assert.Equal(new[] { 3, 2, 1 }, order);
        await system.DisposeAsync();                                      // 重复 Dispose 幂等
        Assert.Equal(3, order.Count);
    }

    private sealed class CustomProductLoader : ILoader
    {
        private readonly Func<ResourceKey, IResource> _produce;
        public CustomProductLoader(Func<ResourceKey, IResource> produce) => _produce = produce;
        public Task<IResource> LoadAsync(ResourceKey key, IProgress<OperationProgress> progress, CancellationToken cancellation)
            => Task.FromResult(_produce(key));
    }

    /// <summary> 同步进度回调 (Progress&lt;T&gt; 会投递到线程池，测试里直接同步收) </summary>
    private sealed class SyncProgress : IProgress<OperationProgress>
    {
        private readonly Action<OperationProgress> _handler;
        public SyncProgress(Action<OperationProgress> handler) => _handler = handler;
        public void Report(OperationProgress value) => _handler(value);
    }

    private sealed class TrackingExecutionContext : IExecutionContext
    {
        private readonly AsyncLocal<bool> _current = new();
        public bool IsCurrent => _current.Value;

        public void Post(Action action)
        {
            var prev = _current.Value;
            _current.Value = true;
            try { action(); }
            finally { _current.Value = prev; }
        }

        public Task RunAsync(Action action) => RunAsync(() => { action(); return true; });

        public Task<T> RunAsync<T>(Func<T> function)
        {
            var prev = _current.Value;
            _current.Value = true;
            try { return Task.FromResult(function()); }
            finally { _current.Value = prev; }
        }

        public async Task<T> InvokeAsync<T>(Func<Task<T>> function)
        {
            var prev = _current.Value;
            _current.Value = true;
            Task<T> task;
            try { task = function(); }
            finally { _current.Value = prev; }
            return await task.ConfigureAwait(false);
        }
    }
}
