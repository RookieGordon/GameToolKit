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
        await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { }))).WaitAsync(Timeout);

        var failed = tasks.Where(t => t.IsFaulted).ToArray();
        var succeeded = tasks.Where(t => t.Status == TaskStatus.RanToCompletion).ToArray();
        Assert.Single(failed);                                        // 只结算一个承担者
        Assert.Equal(2, succeeded.Length);
        var error = Assert.IsType<ResourceException>(failed[0].Exception!.InnerException);
        Assert.IsType<IOException>(error.ResourceError.Cause);
        Assert.Equal(2, loader.LoadCount);
        foreach (var task in succeeded) task.Result.Dispose();
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
    public async Task PreloadThenLoadHitsCache_ReferencesAreIndependent()
    {
        var loader = new FakeLoader();
        await using var system = await TestSystem.CreateAsync(loader);

        await system.PreloadAsync<string>(ResourceSource.Local, "x");
        var a = await system.LoadAsync<string>(ResourceSource.Local, "x");
        var b = await system.LoadAsync<string>(ResourceSource.Local, "x");
        Assert.Equal(1, loader.LoadCount);                            // 预加载后全部命中缓存
        Assert.Same(a.Value, b.Value);

        a.Dispose();
        a.Dispose();                                                  // 幂等
        Assert.False(a.IsValid);
        Assert.Throws<ResourceException>(() => a.Value);              // A 归还后立即失效
        Assert.True(b.IsValid);
        Assert.Equal("product:x", b.Value);                           // B 不受影响

        var c = b.Retain();                                           // 追加持有是独立凭证
        b.Dispose();
        Assert.False(b.IsValid);
        Assert.Equal("product:x", c.Value);
        var entry = Assert.Single(system.Snapshot().Entries);
        Assert.Equal(1, entry.Holds);
        c.Dispose();
        Assert.Equal(0, Assert.Single(system.Snapshot().Entries).Holds);
        Assert.Throws<ResourceException>(() => c.Retain());           // 失效凭证不能再追加
    }

    [Fact]
    public async Task UnloadSkipsHeld_ReleasesUnheld_FailureBecomesResidueWithoutBlockingKey()
    {
        var loader = new FakeLoader();
        await using var system = await TestSystem.CreateAsync(loader);

        using var held = await system.LoadAsync<string>(ResourceSource.Local, "held");
        await system.PreloadAsync<string>(ResourceSource.Local, "free");
        await system.PreloadAsync<string>(ResourceSource.Local, "broken");
        var brokenProduct = loader.Products[2];
        brokenProduct.ReleaseError = new InvalidOperationException("销毁失败");

        var result = await system.UnloadAsync();
        Assert.Equal("held", Assert.Single(result.SkippedInUse).Address);
        Assert.Equal("free", Assert.Single(result.Released).Address);
        Assert.Equal("broken", Assert.Single(result.Failed).Key.Address);
        Assert.Equal(1, loader.Products[1].Released);
        Assert.Equal(0, loader.Products[0].Released);
        Assert.Single(system.Snapshot().Residues);

        // 失败不封锁资源键：同键新加载得到新产物，旧残留仍可卸载
        using (var again = await system.LoadAsync<string>(ResourceSource.Local, "broken"))
        {
            Assert.Equal(4, loader.LoadCount);
            Assert.NotSame(brokenProduct, loader.Products[3]);
            Assert.Same(loader.Products[3].Value, again.Value);
        }
        brokenProduct.ReleaseError = null;
        var retry = await system.UnloadAsync(key => key.Address == "broken");
        Assert.Equal(2, retry.Released.Count);                        // 残留 + 新产物
        Assert.Empty(retry.Failed);
        Assert.Equal(2, brokenProduct.Released);
        Assert.Empty(system.Snapshot().Residues);
        Assert.Equal("held", Assert.Single(system.Snapshot().Entries).Key.Address);
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
    public async Task DisposeStopsNewLoads_UnloadsUnheld_KeepsHeld()
    {
        var loader = new FakeLoader();
        var system = await TestSystem.CreateAsync(loader);
        using var held = await system.LoadAsync<string>(ResourceSource.Local, "held");
        await system.PreloadAsync<string>(ResourceSource.Local, "free");

        await system.DisposeAsync();
        await system.DisposeAsync();
        Assert.Equal(1, loader.Products[1].Released);
        Assert.Equal(0, loader.Products[0].Released);                 // 仍被持有：不强制释放
        Assert.Equal("product:held", held.Value);
        var error = await Assert.ThrowsAsync<ResourceException>(() => system.LoadAsync<string>(ResourceSource.Local, "x"));
        Assert.Equal(DiagnosticCodes.LifecycleSystemDisposed, error.ResourceError.DiagnosticCode);
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

    /// <summary> 同步进度回调 (Progress&lt;T&gt; 会投递到线程池，测试里直接同步收) </summary>
    private sealed class SyncProgress : IProgress<OperationProgress>
    {
        private readonly Action<OperationProgress> _handler;
        public SyncProgress(Action<OperationProgress> handler) => _handler = handler;
        public void Report(OperationProgress value) => _handler(value);
    }
}
