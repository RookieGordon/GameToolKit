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
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var requests = Enumerable.Range(0, 8).Select(_ => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local)).ToArray();
            await loader.FirstAttemptStarted.Task;
            loader.AllowFirstAttempt.TrySetResult(true);

            var failures = await Task.WhenAll(requests.Select(V2Test.Capture));
            var errors = failures.Select(e => Assert.IsType<ResourceLoadException>(e).Error).ToArray();
            Assert.Single(errors.Select(e => e.DiagnosticId).Distinct());
            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(1, diagnostics.CountByCode(DiagnosticCodes.AssetLoadFailed));

            var retry = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            Assert.Equal(2, loader.LoadCount);
            retry.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task RemoteFailure_OnlyCurrentRequestFails_NextRequestLoadsAndRestReuse()
        {
            var manager = V2Test.NewManager(out _, out var diagnostics);
            var loader = new FirstAttemptGateLoader(failures: 1);
            manager.RegisterLoader(ResourceSource.Local, loader, new LoaderPolicy
            {
                FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests,
            });
            await manager.InitializeAsync();

            var first = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            await loader.FirstAttemptStarted.Task;
            var waiting = Enumerable.Range(0, 7).Select(_ => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local)).ToArray();
            loader.AllowFirstAttempt.TrySetResult(true);

            await Assert.ThrowsAsync<ResourceLoadException>(() => first);
            var references = await Task.WhenAll(waiting);
            Assert.Equal(2, loader.LoadCount);
            Assert.All(references, reference => Assert.Same(references[0].Value, reference.Value));
            Assert.All(references.Skip(1), reference => Assert.NotSame(references[0], reference));
            Assert.Equal(1, diagnostics.CountByCode(DiagnosticCodes.AssetLoadFailed));

            foreach (var reference in references) reference.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task RemoteFailure_EachRequestGetsOneAttempt_NoImplicitRetryLoop()
        {
            var manager = V2Test.NewManager(out _, out var diagnostics);
            var loader = new FirstAttemptGateLoader(failures: 4);
            manager.RegisterLoader(ResourceSource.Local, loader, new LoaderPolicy
            {
                FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests,
            });
            await manager.InitializeAsync();

            var requests = Enumerable.Range(0, 4).Select(_ => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local)).ToArray();
            await loader.FirstAttemptStarted.Task;
            loader.AllowFirstAttempt.TrySetResult(true);

            var failures = await Task.WhenAll(requests.Select(V2Test.Capture));
            var errors = failures.Select(e => Assert.IsType<ResourceLoadException>(e).Error).ToArray();
            Assert.Equal(4, loader.LoadCount);
            Assert.Equal(4, errors.Select(e => e.DiagnosticId).Distinct().Count());
            Assert.Equal(4, diagnostics.CountByCode(DiagnosticCodes.AssetLoadFailed));
            manager.Dispose();
        }

        [Theory]
        [InlineData(LoadFailurePolicy.FailWaitingRequests)]
        [InlineData(LoadFailurePolicy.ContinueWaitingRequests)]
        public async Task CallerCancellation_DoesNotFailOtherWaitingRequests(LoadFailurePolicy policy)
        {
            var manager = V2Test.NewManager(out _, out var diagnostics);
            var loader = new FirstAttemptGateLoader(failures: 0);
            manager.RegisterLoader(ResourceSource.Local, loader, new LoaderPolicy { FailurePolicy = policy });
            await manager.InitializeAsync();
            using var cancellation = new CancellationTokenSource();

            var first = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local, cancellationToken: cancellation.Token);
            await loader.FirstAttemptStarted.Task;
            var second = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            loader.AllowFirstAttempt.TrySetResult(true);

            var reference = await second;
            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(0, diagnostics.ReportCount);
            reference.Dispose();
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount);
        }

        [Fact]
        public async Task LoaderPolicy_IsFrozenAtRegistration()
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FirstAttemptGateLoader(failures: 1);
            var policy = new LoaderPolicy { FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests };
            manager.RegisterLoader(ResourceSource.Local, loader, policy);
            await manager.InitializeAsync();
            policy.FailurePolicy = LoadFailurePolicy.FailWaitingRequests;

            var first = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            await loader.FirstAttemptStarted.Task;
            var second = manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
            loader.AllowFirstAttempt.TrySetResult(true);

            await Assert.ThrowsAsync<ResourceLoadException>(() => first);
            var reference = await second;
            Assert.Equal(2, loader.LoadCount);
            reference.Dispose();
            manager.Dispose();
        }

        [Fact]
        public void InvalidFailurePolicy_IsRejectedDuringRegistration()
        {
            using var manager = V2Test.NewManager(out _, out _);
            Assert.Throws<ArgumentOutOfRangeException>(() => manager.RegisterLoader(ResourceSource.Local, new FakeLoader(),
                new LoaderPolicy { FailurePolicy = (LoadFailurePolicy)123 }));
        }

        [Fact]
        public void NegativeConcurrency_IsRejectedDuringRegistration()
        {
            using var manager = V2Test.NewManager(out _, out _);
            Assert.Throws<ArgumentOutOfRangeException>(() => manager.RegisterLoader(ResourceSource.Local, new FakeLoader(),
                new LoaderPolicy { MaxConcurrentLoads = -1 }));
        }

        [Theory]
        [InlineData(CleanupStatus.Unknown)]
        [InlineData(CleanupStatus.Incomplete)]
        public async Task RemoteFailure_UnfinishedCleanupBlocksWaitingAndFutureRequests(CleanupStatus cleanup)
        {
            var manager = V2Test.NewManager(out _, out _);
            var loader = new FirstAttemptGateLoader(failures: 1, cleanup: cleanup);
            manager.RegisterLoader(ResourceSource.Local, loader, new LoaderPolicy
            {
                FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests,
            });
            await manager.InitializeAsync();

            var requests = Enumerable.Range(0, 4).Select(_ => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local)).ToArray();
            await loader.FirstAttemptStarted.Task;
            loader.AllowFirstAttempt.TrySetResult(true);
            var failures = await Task.WhenAll(requests.Select(V2Test.Capture));
            var errors = failures.Select(e => Assert.IsType<ResourceLoadException>(e).Error).ToArray();

            Assert.Single(errors.Select(e => e.DiagnosticId).Distinct());
            var future = await Assert.ThrowsAsync<ResourceLoadException>(() => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local));
            Assert.Equal(errors[0].DiagnosticId, future.Error.DiagnosticId);
            Assert.Equal(1, loader.LoadCount);
            manager.Dispose();
            Assert.True(manager.GetSnapshot().IsDisposed);
        }

        [Fact]
        public async Task RemoteRequests_AllCancel_LateSuccessIsReleasedWithoutDelivery()
        {
            var manager = V2Test.NewManager(out _, out var diagnostics);
            var loader = new FirstAttemptGateLoader(failures: 0, ignoreCancellation: true);
            manager.RegisterLoader(ResourceSource.Local, loader, new LoaderPolicy
            {
                FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests,
            });
            await manager.InitializeAsync();
            using var cancellation = new CancellationTokenSource();
            var requests = Enumerable.Range(0, 4)
                .Select(_ => manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local, cancellationToken: cancellation.Token)).ToArray();
            await loader.FirstAttemptStarted.Task;

            cancellation.Cancel();
            var failures = await Task.WhenAll(requests.Select(V2Test.Capture));
            Assert.All(failures, failure => Assert.IsAssignableFrom<OperationCanceledException>(failure));
            loader.AllowFirstAttempt.TrySetResult(true);
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1
                && manager.GetSnapshot().ResourceRows.Count == 0);

            Assert.Equal(1, loader.LoadCount);
            Assert.Equal(1, loader.ReleaseCount);
            Assert.Equal(0, diagnostics.ReportCount);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SharedResult_CachesOnlyWhenAtLeastOneRequestReceivedAReference(bool firstCanUseResult)
        {
            var manager = new ResourceManager(new ImmediateExecutionContext(), new ResourceSystemOptions
            {
                RequestTimeout = null,
                Memory = new MemoryPolicy { IdleLifetime = TimeSpan.FromMinutes(1) },
            });
            var loader = new FirstAttemptGateLoader(failures: 0);
            manager.RegisterLoader(ResourceSource.Local, loader);
            await manager.InitializeAsync();

            var firstSuccess = firstCanUseResult ? manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local) : null;
            var firstMismatch = firstCanUseResult ? null : manager.LoadAsync<string>("a", source: ResourceSource.Local);
            var lastRequest = manager.LoadAsync<string>("a", source: ResourceSource.Local);
            Assert.Equal(2, Assert.Single(manager.GetSnapshot().ResourceRows).WaitingCount);

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
            var mismatch = await Assert.ThrowsAsync<ResourceLoadException>(
                () => lastRequest.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(DiagnosticCodes.AssetTypeMismatch, mismatch.Error.DiagnosticCode);
            if (firstCanUseResult)
            {
                Assert.Equal(ResourceState.Idle, Assert.Single(manager.GetSnapshot().ResourceRows).State);
                Assert.Equal(0, loader.ReleaseCount);
                var reused = await manager.LoadAsync<FakeAsset>("a", source: ResourceSource.Local);
                Assert.Same(value, reused.Value);
                reused.Dispose();
            }
            else
            {
                // 领取失败先返回；无人使用产物的释放由独立操作完成，不能要求同步卸载。
                await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1
                    && manager.GetSnapshot().ResourceRows.Count == 0);
                Assert.Empty(manager.GetSnapshot().ResourceRows);
                Assert.Equal(1, loader.ReleaseCount);
            }
            Assert.Equal(1, loader.LoadCount);
            manager.Dispose();
            await V2Test.WaitUntilAsync(() => loader.ReleaseCount == 1);
            Assert.Equal(1, loader.ReleaseCount);
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
