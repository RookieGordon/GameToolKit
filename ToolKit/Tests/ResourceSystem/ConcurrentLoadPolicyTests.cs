using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class ConcurrentLoadPolicyTests
    {
        [Fact]
        public async Task LocalFailure_FailsCurrentWaitersOnce_NewRequestCanRetry()
        {
            var manager = V2Test.NewManager(out _, out var diagnostics);
            var loader = new FirstAttemptGateLoader(failures: 1);
            manager.RegisterLoader("fake", loader);

            var requests = Enumerable.Range(0, 8).Select(_ => manager.LoadAsync<FakeAsset>("a")).ToArray();
            await loader.FirstAttemptStarted.Task;
            loader.AllowFirstAttempt.TrySetResult(true);

            var failures = await Task.WhenAll(requests.Select(V2Test.Capture));
            var errors = failures.Select(e => Assert.IsType<ResourceLoadException>(e).Error).ToArray();
            Assert.Single(errors.Select(e => e.DiagnosticId).Distinct());
            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(1, diagnostics.CountByCode(DiagnosticCodes.AssetLoadFailed));

            var retry = await manager.LoadAsync<FakeAsset>("a");
            Assert.Equal(2, loader.LoadCount);
            retry.Dispose();
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task RemoteFailure_OnlyCurrentRequestFails_NextRequestLoadsAndRestReuse()
        {
            var manager = V2Test.NewManager(out _, out var diagnostics);
            var loader = new FirstAttemptGateLoader(failures: 1);
            manager.RegisterLoader("fake", loader, new LoaderPolicy
            {
                FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests,
            });

            var first = manager.LoadAsync<FakeAsset>("a");
            await loader.FirstAttemptStarted.Task;
            var waiting = Enumerable.Range(0, 7).Select(_ => manager.LoadAsync<FakeAsset>("a")).ToArray();
            loader.AllowFirstAttempt.TrySetResult(true);

            await Assert.ThrowsAsync<ResourceLoadException>(() => first);
            var references = await Task.WhenAll(waiting);
            Assert.Equal(2, loader.LoadCount);
            Assert.All(references, reference => Assert.Same(references[0].Value, reference.Value));
            Assert.All(references.Skip(1), reference => Assert.NotSame(references[0], reference));
            Assert.Equal(1, diagnostics.CountByCode(DiagnosticCodes.AssetLoadFailed));

            foreach (var reference in references) reference.Dispose();
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task RemoteFailure_EachRequestGetsOneAttempt_NoImplicitRetryLoop()
        {
            var manager = V2Test.NewManager(out _, out var diagnostics);
            var loader = new FirstAttemptGateLoader(failures: 4);
            manager.RegisterLoader("fake", loader, new LoaderPolicy
            {
                FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests,
            });

            var requests = Enumerable.Range(0, 4).Select(_ => manager.LoadAsync<FakeAsset>("a")).ToArray();
            await loader.FirstAttemptStarted.Task;
            loader.AllowFirstAttempt.TrySetResult(true);

            var failures = await Task.WhenAll(requests.Select(V2Test.Capture));
            var errors = failures.Select(e => Assert.IsType<ResourceLoadException>(e).Error).ToArray();
            Assert.Equal(4, loader.LoadCount);
            Assert.Equal(4, errors.Select(e => e.DiagnosticId).Distinct().Count());
            Assert.Equal(4, diagnostics.CountByCode(DiagnosticCodes.AssetLoadFailed));
            await manager.ShutdownAsync();
        }

        [Theory]
        [InlineData(LoadFailurePolicy.FailWaitingRequests)]
        [InlineData(LoadFailurePolicy.ContinueWaitingRequests)]
        public async Task CallerCancellation_DoesNotFailOtherWaitingRequests(LoadFailurePolicy policy)
        {
            var manager = V2Test.NewManager(out _, out var diagnostics);
            var loader = new FirstAttemptGateLoader(failures: 0);
            manager.RegisterLoader("fake", loader, new LoaderPolicy { FailurePolicy = policy });
            using var cancellation = new CancellationTokenSource();

            var first = manager.LoadAsync<FakeAsset>("a", cancellationToken: cancellation.Token);
            await loader.FirstAttemptStarted.Task;
            var second = manager.LoadAsync<FakeAsset>("a");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            loader.AllowFirstAttempt.TrySetResult(true);

            var reference = await second;
            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(0, diagnostics.ReportCount);
            reference.Dispose();
            await manager.ShutdownAsync();
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task LoaderPolicy_IsFrozenAtRegistration()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FirstAttemptGateLoader(failures: 1);
            var policy = new LoaderPolicy { FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests };
            manager.RegisterLoader("fake", loader, policy);
            policy.FailurePolicy = LoadFailurePolicy.FailWaitingRequests;

            var first = manager.LoadAsync<FakeAsset>("a");
            await loader.FirstAttemptStarted.Task;
            var second = manager.LoadAsync<FakeAsset>("a");
            loader.AllowFirstAttempt.TrySetResult(true);

            await Assert.ThrowsAsync<ResourceLoadException>(() => first);
            var reference = await second;
            Assert.Equal(2, loader.LoadCount);
            reference.Dispose();
            await manager.ShutdownAsync();
        }

        [Fact]
        public void InvalidFailurePolicy_IsRejectedDuringRegistration()
        {
            using var manager = V2Test.NewManager(out _, out _);
            Assert.Throws<ArgumentOutOfRangeException>(() => manager.RegisterLoader("fake", new FakeLoader(),
                new LoaderPolicy { FailurePolicy = (LoadFailurePolicy)123 }));
        }

        [Fact]
        public void NegativeConcurrency_IsRejectedDuringRegistration()
        {
            using var manager = V2Test.NewManager(out _, out _);
            Assert.Throws<ArgumentOutOfRangeException>(() => manager.RegisterLoader("fake", new FakeLoader(),
                new LoaderPolicy { MaxConcurrentLoads = -1 }));
        }

        [Theory]
        [InlineData(CleanupStatus.Unknown)]
        [InlineData(CleanupStatus.Incomplete)]
        public async Task RemoteFailure_UnfinishedCleanupBlocksWaitingAndFutureRequests(CleanupStatus cleanup)
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FirstAttemptGateLoader(failures: 1, cleanup: cleanup);
            manager.RegisterLoader("fake", loader, new LoaderPolicy
            {
                FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests,
            });

            var requests = Enumerable.Range(0, 4).Select(_ => manager.LoadAsync<FakeAsset>("a")).ToArray();
            await loader.FirstAttemptStarted.Task;
            loader.AllowFirstAttempt.TrySetResult(true);
            var failures = await Task.WhenAll(requests.Select(V2Test.Capture));
            var errors = failures.Select(e => Assert.IsType<ResourceLoadException>(e).Error).ToArray();

            Assert.Single(errors.Select(e => e.DiagnosticId).Distinct());
            var future = await Assert.ThrowsAsync<ResourceLoadException>(() => manager.LoadAsync<FakeAsset>("a"));
            Assert.Equal(errors[0].DiagnosticId, future.Error.DiagnosticId);
            Assert.Equal(1, loader.LoadCount);
            await Assert.ThrowsAsync<AggregateException>(() => manager.ShutdownAsync());
        }

        [Fact]
        public async Task RemoteRequests_AllCancel_LateSuccessIsReleasedWithoutDelivery()
        {
            var manager = V2Test.NewManager(out _, out var diagnostics);
            var loader = new FirstAttemptGateLoader(failures: 0, ignoreCancellation: true);
            manager.RegisterLoader("fake", loader, new LoaderPolicy
            {
                FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests,
            });
            using var cancellation = new CancellationTokenSource();
            var requests = Enumerable.Range(0, 4)
                .Select(_ => manager.LoadAsync<FakeAsset>("a", cancellationToken: cancellation.Token)).ToArray();
            await loader.FirstAttemptStarted.Task;

            cancellation.Cancel();
            var failures = await Task.WhenAll(requests.Select(V2Test.Capture));
            Assert.All(failures, failure => Assert.IsAssignableFrom<OperationCanceledException>(failure));
            loader.AllowFirstAttempt.TrySetResult(true);
            await manager.ShutdownAsync();

            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(1, loader.ReleaseCount);
            Assert.Equal(0, diagnostics.ReportCount);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task LastWaitingRequestCannotUseResult_CachesOnlyPreviouslyReferencedAssets(bool firstCanUseResult)
        {
            var context = new PausableCallerContext();
            var manager = new ResourceManager(context, new ResourceSystemOptions
            {
                DefaultLoader = "fake",
                RequestTimeout = null,
                Memory = new MemoryPolicy { IdleLifetime = TimeSpan.FromMinutes(1) },
            });
            var loader = new FirstAttemptGateLoader(failures: 0);
            manager.RegisterLoader("fake", loader);

            var firstSuccess = firstCanUseResult ? manager.LoadAsync<FakeAsset>("a") : null;
            var firstMismatch = firstCanUseResult ? null : manager.LoadAsync<string>("a");
            var lastRequest = context.StartPausableCaller(() => manager.LoadAsync<string>("a"));
            Assert.Equal(2, Assert.Single(manager.GetSnapshot().ResourceRows).WaitingCount);

            // B 已登记但尚未取得资源锁；暂停 B 接下来的上下文提交，让 A 可以先完成并归还引用。
            context.PauseCaller = true;
            loader.AllowFirstAttempt.TrySetResult(true);
            FakeAsset? value = null;
            if (firstSuccess != null)
            {
                var reference = await firstSuccess.WaitAsync(TimeSpan.FromSeconds(5));
                value = reference.Value;
                reference.Dispose();
            }
            else
            {
                await Assert.ThrowsAsync<ResourceLoadException>(() => firstMismatch!.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            await context.CallerPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(lastRequest.IsCompleted);
            Assert.Equal(0, loader.ReleaseCount);
            var waitingRow = Assert.Single(manager.GetSnapshot().ResourceRows);
            Assert.Equal(0, waitingRow.HoldCount);
            Assert.Equal(1, waitingRow.WaitingCount);

            context.ResumeCaller();
            var mismatch = await Assert.ThrowsAsync<ResourceLoadException>(
                () => lastRequest.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(DiagnosticCodes.AssetTypeMismatch, mismatch.Error.DiagnosticCode);
            if (firstCanUseResult)
            {
                Assert.Equal(ResourceState.Idle, Assert.Single(manager.GetSnapshot().ResourceRows).State);
                Assert.Equal(0, loader.ReleaseCount);
                var reused = await manager.LoadAsync<FakeAsset>("a");
                Assert.Same(value, reused.Value);
                reused.Dispose();
            }
            else
            {
                Assert.Empty(manager.GetSnapshot().ResourceRows);
                Assert.Equal(1, loader.ReleaseCount);
            }
            Assert.Equal(1, loader.LoadCount);
            await manager.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, loader.ReleaseCount);
        }

        /// <summary> 只暂停指定调用者的后续提交；其他请求继续推进，测试不依赖线程调度先后。 </summary>
        private sealed class PausableCallerContext : IExecutionContext
        {
            private readonly IExecutionContext _inner = new ImmediateExecutionContext();
            private readonly AsyncLocal<bool> _pausableCaller = new AsyncLocal<bool>();
            private readonly TaskCompletionSource<bool> _resume =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> CallerPaused =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public volatile bool PauseCaller;

            public Task<T> StartPausableCaller<T>(Func<Task<T>> start)
            {
                _pausableCaller.Value = true;
                try { return start(); }
                finally { _pausableCaller.Value = false; }
            }

            public void ResumeCaller()
            {
                PauseCaller = false;
                _resume.TrySetResult(true);
            }

            private Task WaitForCaller()
            {
                if (!_pausableCaller.Value || !PauseCaller) return Task.CompletedTask;
                CallerPaused.TrySetResult(true);
                return _resume.Task;
            }

            public bool IsCurrent => _inner.IsCurrent;
            public void AssertAccess() => _inner.AssertAccess();
            public void Post(Action action) => _inner.Post(action);
            public void Invoke(Action action) => _inner.Invoke(action);
            public T Invoke<T>(Func<T> function) => _inner.Invoke(function);
            public Task<T> InvokeAsync<T>(Func<Task<T>> function) => _inner.InvokeAsync(function);
            public async Task RunAsync(Action action)
            {
                await WaitForCaller().ConfigureAwait(false);
                await _inner.RunAsync(action).ConfigureAwait(false);
            }
            public async Task<T> RunAsync<T>(Func<T> function)
            {
                await WaitForCaller().ConfigureAwait(false);
                return await _inner.RunAsync(function).ConfigureAwait(false);
            }
        }

        private sealed class FirstAttemptGateLoader : IResourceLoader
        {
            private readonly int _failures;
            private readonly CleanupStatus _cleanup;
            private readonly bool _ignoreCancellation;
            public readonly TaskCompletionSource<bool> FirstAttemptStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> AllowFirstAttempt =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int LoadCount;
            public int ReleaseCount;

            public FirstAttemptGateLoader(int failures, CleanupStatus cleanup = CleanupStatus.Complete,
                bool ignoreCancellation = false)
            {
                _failures = failures;
                _cleanup = cleanup;
                _ignoreCancellation = ignoreCancellation;
            }

            public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new ResolvedResource(request.Address, typeof(FakeAsset)));
            }

            public async Task<LoadedAsset> LoadAsync(ResolvedResource resource,
                IProgress<ResourceProgress> progress, CancellationToken operationToken)
            {
                var attempt = Interlocked.Increment(ref LoadCount);
                if (attempt == 1)
                {
                    FirstAttemptStarted.TrySetResult(true);
                    if (_ignoreCancellation)
                        await AllowFirstAttempt.Task.ConfigureAwait(false);
                    else
                        await AllowFirstAttempt.Task.WaitTestCancellation(operationToken).ConfigureAwait(false);
                }
                if (attempt <= _failures)
                {
                    throw new ResourceLoadException(new LoadError(DiagnosticCodes.AssetLoadFailed,
                        LoadStage.LoadAsset, _cleanup));
                }
                return new LoadedAsset(new FakeAsset(resource.LocalKey), 1, null, () =>
                {
                    Interlocked.Increment(ref ReleaseCount);
                    return Task.CompletedTask;
                });
            }
        }
    }
}
