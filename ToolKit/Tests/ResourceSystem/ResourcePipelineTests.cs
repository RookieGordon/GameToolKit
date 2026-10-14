/*
 * 主流程行为用例：加载 → 合并 → 交付 → 归还 → 卸载，以及失败策略、取消与收尾。
 * 断言用可直接观察的契约：加载次数、持有数、是否可复用、释放对象与诊断码。
 * `Value` 是引擎调用，只在状态串行域内读取，因此读取统一经 ReadValue 在域内执行。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class ResourcePipelineTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        private static ResourceManager NewManager(TestExecutionContext context, out FakeLoader loader,
            LoadFailurePolicy failurePolicy = LoadFailurePolicy.FailWaitingRequests,
            int maxConcurrentLoads = 4)
        {
            loader = new FakeLoader();
            var manager = new ResourceManager(context, diagnostics: new RecordingDiagnostics());
            manager.RegisterLoader(ResourceSource.Local, loader, new LoaderPolicy
            {
                MaxConcurrentLoads = maxConcurrentLoads,
                FailurePolicy = failurePolicy,
            });
            return manager;
        }

        /// <summary> 在状态串行域内读取引用值；引用本身可以跨线程持有与归还 </summary>
        private static T ReadValue<T>(TestExecutionContext context, ResourceRef<T> reference) where T : class =>
            context.Invoke(() => reference.Value);

        private static async Task<T> WithTimeout<T>(Task<T> task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(TestTimeout)).ConfigureAwait(false);
            if (!ReferenceEquals(winner, task)) throw new TimeoutException($"等待 {what} 超时");
            return await task.ConfigureAwait(false);
        }

        [Fact]
        public async Task 同键并发合并为一次加载且各自取得独立引用()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();

            var first = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            var second = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            var third = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);

            using var a = await first.WaitAsync(TestTimeout);
            using var b = await second.WaitAsync(TestTimeout);
            using var c = await third.WaitAsync(TestTimeout);

            Assert.Equal(1, loader.LoadCount);
            Assert.Same(ReadValue(context, a), ReadValue(context, b));
            Assert.Same(ReadValue(context, b), ReadValue(context, c));

            var row = Assert.Single(manager.GetSnapshot().ResourceRows);
            Assert.Equal(3, row.HoldCount);
            Assert.Equal(ResourceState.Ready, row.State);
        }

        [Fact]
        public async Task 产物闲置后可复用且不再调用后端()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();

            var first = await manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local).WaitAsync(TestTimeout);
            var product = ReadValue(context, first);
            first.Dispose();

            var idle = Assert.Single(manager.GetSnapshot().ResourceRows);
            Assert.Equal(ResourceState.Idle, idle.State);
            Assert.Equal(0, idle.HoldCount);

            using var second = await WithTimeout(
                manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local), "第二次加载");
            Assert.Same(product, ReadValue(context, second));
            Assert.Equal(1, loader.LoadCount);
        }

        [Fact]
        public async Task 追加持有必须分别归还()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out _);
            await manager.InitializeAsync();

            var first = await manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local).WaitAsync(TestTimeout);
            var extra = first.Retain();
            Assert.Equal(2, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);

            first.Dispose();
            Assert.Equal(1, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);
            Assert.True(extra.IsValid);

            extra.Dispose();
            Assert.Equal(0, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);
        }

        [Fact]
        public async Task 主动卸载只释放无人持有的产物并返回计数()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();

            var kept = await manager.LoadAsync<FakeProduct>("kept", ResourceSource.Local).WaitAsync(TestTimeout);
            var dropped = await manager.LoadAsync<FakeProduct>("dropped", ResourceSource.Local).WaitAsync(TestTimeout);
            var keptProduct = ReadValue(context, kept);
            var droppedProduct = ReadValue(context, dropped);
            dropped.Dispose();

            var result = await manager.UnloadUnusedAsync().WaitAsync(TestTimeout);

            Assert.Equal(1, result.ReleasedCount);
            Assert.Equal(1, result.SkippedInUseCount);
            Assert.Empty(result.Failures);
            Assert.True(droppedProduct.Released);
            Assert.False(keptProduct.Released);
            Assert.Single(manager.GetSnapshot().ResourceRows);

            kept.Dispose();
            var second = await manager.UnloadUnusedAsync().WaitAsync(TestTimeout);
            Assert.Equal(1, second.ReleasedCount);
            Assert.True(keptProduct.Released);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
            Assert.Equal(2, loader.LoadCount);
        }

        [Fact]
        public async Task 卸载选择器在状态提交之外执行且可以选择来源()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out _);
            await manager.InitializeAsync();
            var local = await manager.LoadAsync<FakeProduct>("local", ResourceSource.Local).WaitAsync(TestTimeout);
            local.Dispose();

            var selected = new List<ResourceRow>();
            var unloadTask = manager.UnloadUnusedAsync(row =>
            {
                selected.Add(row);
                return row.Key.Source == ResourceSource.Local;
            });
            var result = await unloadTask.WaitAsync(TestTimeout);

            Assert.Equal(1, result.ReleasedCount);
            Assert.Single(selected);
        }

        [Fact]
        public async Task 请求类型与本轮产物不符只让该项失败()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();

            var good = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            var bad = manager.LoadAsync<string>("asset", ResourceSource.Local);

            using var reference = await good.WaitAsync(TestTimeout);
            var error = await Assert.ThrowsAsync<ResourceLoadException>(() => bad.WaitAsync(TestTimeout));

            Assert.Equal(DiagnosticCodes.AssetTypeMismatch, error.Error.DiagnosticCode);
            Assert.Equal(1, loader.LoadCount);
            Assert.Equal("asset", ReadValue(context, reference).Name);
        }

        [Fact]
        public async Task 共同失败策略下本轮等待者一起失败且后续请求可以再试()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();
            var attempts = 0;
            loader.OnLoad = (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    return Task.FromException<LoadedAsset>(new ResourceLoadException(new LoadError(
                        DiagnosticCodes.AssetLoadFailed, LoadStage.LoadAsset, CleanupStatus.Complete)));
                }
                return Task.FromResult(new LoadedAsset(new FakeProduct("late")));
            };

            var first = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            var second = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);

            var firstError = await Assert.ThrowsAsync<ResourceLoadException>(() => first.WaitAsync(TestTimeout));
            var secondError = await Assert.ThrowsAsync<ResourceLoadException>(() => second.WaitAsync(TestTimeout));
            Assert.Equal(DiagnosticCodes.AssetLoadFailed, firstError.Error.DiagnosticCode);
            Assert.Equal(DiagnosticCodes.AssetLoadFailed, secondError.Error.DiagnosticCode);
            Assert.Equal(1, loader.LoadCount);

            using var reference = await manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local).WaitAsync(TestTimeout);
            Assert.Equal("late", ReadValue(context, reference).Name);
            Assert.Equal(2, loader.LoadCount);
        }

        [Fact]
        public async Task 接续尝试策略下归属者失败其余请求重新解析并成功()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader,
                failurePolicy: LoadFailurePolicy.ContinueWaitingRequests);
            await manager.InitializeAsync();
            var attempts = 0;
            loader.OnLoad = (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    return Task.FromException<LoadedAsset>(new ResourceLoadException(new LoadError(
                        DiagnosticCodes.NetworkHttpServerError, LoadStage.Download, CleanupStatus.Complete)));
                }
                return Task.FromResult(new LoadedAsset(new FakeProduct("second-round")));
            };

            var leader = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            await V2Test.WaitUntilAsync(() => loader.LoadCount == 1);
            var follower = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);

            var leaderError = await Assert.ThrowsAsync<ResourceLoadException>(() => leader.WaitAsync(TestTimeout));
            Assert.Equal(DiagnosticCodes.NetworkHttpServerError, leaderError.Error.DiagnosticCode);

            // 没有新的业务调用：后继请求自己重新解析并参加下一轮。
            using var reference = await follower.WaitAsync(TestTimeout);
            Assert.Equal("second-round", ReadValue(context, reference).Name);
            Assert.Equal(2, loader.LoadCount);
            Assert.Equal(2, loader.ResolveCount);
        }

        [Fact]
        public async Task 接续尝试下每个请求最多承担一次失败()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader,
                failurePolicy: LoadFailurePolicy.ContinueWaitingRequests);
            await manager.InitializeAsync();
            // 后端每次都失败：接续不能变成隐式无限重试。
            loader.OnLoad = (_, _) => Task.FromException<LoadedAsset>(new ResourceLoadException(new LoadError(
                DiagnosticCodes.NetworkHttpServerError, LoadStage.Download, CleanupStatus.Complete)));

            // 先让两个请求稳定合并到同一轮，再让第三个加入：每轮由尚未承担失败的请求接续。
            var first = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            await V2Test.WaitUntilAsync(() => loader.LoadCount == 1);
            var second = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            await V2Test.WaitUntilAsync(() => loader.LoadCount == 2);
            var third = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);

            var tasks = new List<Task<ResourceRef<FakeProduct>>> { first, second, third };
            var all = Task.WhenAll(tasks);
            var winner = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(ReferenceEquals(winner, all),
                $"未全部完成：loads={loader.LoadCount} resolves={loader.ResolveCount} " +
                $"states=[{string.Join(",", tasks.ConvertAll(t => t.Status.ToString()))}]");

            foreach (var task in tasks)
            {
                var error = await Assert.ThrowsAsync<ResourceLoadException>(() => task.WaitAsync(TestTimeout));
                Assert.Equal(DiagnosticCodes.NetworkHttpServerError, error.Error.DiagnosticCode);
            }
            // 每个请求最多承担一次失败：接续轮数不超过请求个数，没有隐式无限重试。
            Assert.True(loader.LoadCount <= 3, $"实际加载次数 {loader.LoadCount} 超过请求个数");
        }

        [Fact]
        public async Task 归属者取消不影响剩余请求()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runs = 0;
            loader.OnLoad = async (_, token) =>
            {
                if (Interlocked.Increment(ref runs) == 1)
                {
                    started.TrySetResult(true);
                    // 不观察取消令牌：模拟"取消不打断已经在跑的后端"。
                    await gate.Task.ConfigureAwait(false);
                }
                return new LoadedAsset(new FakeProduct("after-cancel"));
            };

            using var cancellation = new CancellationTokenSource();
            Exception? leaderFailure = null;
            var leader = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local, ct: cancellation.Token);
            await started.Task.WaitAsync(TestTimeout);

            cancellation.Cancel();
            try
            {
                await leader.WaitAsync(TestTimeout);
            }
            catch (Exception error)
            {
                leaderFailure = error;
            }

            // 归属者已经退出；剩余请求要么在同轮后端被取消时按取消结束，要么自己重新解析成功。
            Task<ResourceRef<FakeProduct>> follower;
            try
            {
                follower = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            }
            catch (Exception error)
            {
                Assert.True(false, $"发起后继请求失败：{error.GetType().Name}: {error.Message}");
                return;
            }
            gate.TrySetResult(true);
            try
            {
                using var reference = await follower.WaitAsync(TestTimeout);
                Assert.Equal("after-cancel", ReadValue(context, reference).Name);
            }
            catch (OperationCanceledException)
            {
                // 归属者取消时同轮后端已被取消：后继请求按取消结束，但必须能重新发起并成功。
                using var retried = await manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local)
                    .WaitAsync(TestTimeout);
                Assert.Equal("after-cancel", ReadValue(context, retried).Name);
            }
        }

        [Fact]
        public async Task 归属者取消后不把失败转嫁给剩余请求()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader,
                failurePolicy: LoadFailurePolicy.ContinueWaitingRequests);
            await manager.InitializeAsync();

            var attempts = 0;
            loader.OnLoad = (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    return Task.FromException<LoadedAsset>(new ResourceLoadException(new LoadError(
                        DiagnosticCodes.NetworkHttpServerError, LoadStage.Download, CleanupStatus.Complete)));
                }
                return Task.FromResult(new LoadedAsset(new FakeProduct("after-cancel")));
            };

            using var cancellation = new CancellationTokenSource();
            var leader = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local, ct: cancellation.Token);
            await V2Test.WaitUntilAsync(() => loader.LoadCount == 1);
            var follower = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            // 归属者收到本轮失败并退出等待；此后取消它只是空转，不得转嫁给剩余请求。
            await leader.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TestTimeout);

            cancellation.Cancel();
            using var reference = await follower.WaitAsync(TestTimeout);
            Assert.Equal("after-cancel", ReadValue(context, reference).Name);
        }

        [Fact]
        public async Task 全部等待者取消后新请求只等旧记录结束再自己加载()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var backendToken = CancellationToken.None;
            var firstRun = true;
            loader.OnLoad = async (_, token) =>
            {
                if (!firstRun) return new LoadedAsset(new FakeProduct("second"));
                firstRun = false;
                backendToken = token;
                started.TrySetResult(true);
                await gate.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new LoadedAsset(new FakeProduct("first"));
            };

            using var cancelled = new CancellationTokenSource();
            var abandoned = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local, ct: cancelled.Token);
            await started.Task.WaitAsync(TestTimeout);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(TestTimeout));
            await V2Test.WaitUntilAsync(() => backendToken.IsCancellationRequested);

            // 旧记录还没收尾：新请求必须等它结束，且不接收它的结果。
            var next = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            gate.TrySetResult(true);
            using var reference = await next.WaitAsync(TestTimeout);
            Assert.Equal("second", ReadValue(context, reference).Name);
            Assert.Equal(2, loader.LoadCount);
        }

        [Fact]
        public async Task 交付通知置位后取消不再夺走已分配的引用()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();

            using var cancellation = new CancellationTokenSource();
            var firstLine = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            loader.OnLoad = async (_, _) =>
            {
                firstLine.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return new LoadedAsset(new FakeProduct("kept"));
            };

            var loading = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local, ct: cancellation.Token);
            await firstLine.Task.WaitAsync(TestTimeout);
            release.TrySetResult(true);

            // 交付提交完成后才使令牌失效：调用者仍必须取得有效引用。
            using var reference = await loading.WaitAsync(TestTimeout);
            cancellation.Cancel();
            await V2Test.WaitUntilAsync(() => cancellation.IsCancellationRequested);

            Assert.Equal("kept", ReadValue(context, reference).Name);
            Assert.True(reference.IsValid);
            Assert.Equal(1, Assert.Single(manager.GetSnapshot().ResourceRows).HoldCount);
        }

        [Fact]
        public async Task 等待侧取消按主动取消完成且不占用额度()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader, maxConcurrentLoads: 1);
            await manager.InitializeAsync();

            var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            loader.OnLoad = async (resource, _) =>
            {
                if (resource.LocalKey == "first")
                {
                    started.TrySetResult(true);
                    await hold.Task.ConfigureAwait(false);
                }
                return new LoadedAsset(new FakeProduct(resource.LocalKey));
            };

            var running = manager.LoadAsync<FakeProduct>("first", ResourceSource.Local);
            await started.Task.WaitAsync(TestTimeout);

            using var cancellation = new CancellationTokenSource();
            var queued = manager.LoadAsync<FakeProduct>("second", ResourceSource.Local, ct: cancellation.Token);
            await V2Test.WaitUntilAsync(() => loader.LoadCount == 1 && loader.ResolveCount >= 2);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TestTimeout));

            hold.TrySetResult(true);
            using var first = await running.WaitAsync(TestTimeout);
            Assert.Equal("first", ReadValue(context, first).Name);
            // 排队取消项不调用后端，也不占住额度。
            Assert.Equal(1, loader.LoadCount);
            using var third = await manager.LoadAsync<FakeProduct>("third", ResourceSource.Local).WaitAsync(TestTimeout);
            Assert.Equal("third", ReadValue(context, third).Name);
        }

        [Fact]
        public async Task 后端第一行不在登记栈内且调用者先返回()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();

            var seen = new List<string>();
            loader.OnLoad = (_, _) =>
            {
                lock (seen) seen.Add("backend-first-line");
                return Task.FromResult(new LoadedAsset(new FakeProduct("x")));
            };

            var loading = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            lock (seen) seen.Add("caller-returned");
            using var reference = await loading.WaitAsync(TestTimeout);

            lock (seen)
            {
                Assert.Equal(2, seen.Count);
                Assert.Equal("caller-returned", seen[0]);
                Assert.Equal("backend-first-line", seen[1]);
            }
        }

        [Fact]
        public async Task 单次调用超时按请求失败报出且不影响其他请求()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();

            var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            loader.OnLoad = async (_, _) =>
            {
                started.TrySetResult(true);
                await hold.Task.ConfigureAwait(false);
                return new LoadedAsset(new FakeProduct("slow"));
            };

            var options = new RequestOptions { Timeout = TimeSpan.FromMilliseconds(50) };
            var slow = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local, options);
            await started.Task.WaitAsync(TestTimeout);
            var error = await Assert.ThrowsAsync<ResourceLoadException>(() => slow.WaitAsync(TestTimeout));
            Assert.Equal(DiagnosticCodes.RequestTimeout, error.Error.DiagnosticCode);

            // 共享后端没有被个人超时取消；此时才发起第二个请求，避免与超时竞速。
            var patient = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            await V2Test.WaitUntilAsync(() => loader.ResolveCount >= 2);
            hold.TrySetResult(true);
            using var reference = await patient.WaitAsync(TestTimeout);
            Assert.Equal("slow", ReadValue(context, reference).Name);
        }

        [Fact]
        public async Task 无人接收的迟到产物由原记录收尾且不留下复用项()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();

            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new FakeProduct("late");
            loader.OnLoad = async (_, _) =>
            {
                started.TrySetResult(true);
                // 无视取消：模拟迟到的产物，收尾必须由原记录完成。
                await finished.Task.ConfigureAwait(false);
                return new LoadedAsset(late, releaseAsync: () =>
                {
                    late.Released = true;
                    return Task.CompletedTask;
                });
            };

            var loading = manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local);
            await started.Task.WaitAsync(TestTimeout);
            manager.Dispose();

            // 退出先让等待者失败，随后迟到的产物仍必须被原记录释放。
            await Assert.ThrowsAsync<ResourceLoadException>(() => loading.WaitAsync(TestTimeout));
            finished.TrySetResult(true);

            await V2Test.WaitUntilAsync(() => late.Released);
            Assert.True(late.Released);
        }

        [Fact]
        public async Task 后端返回失效产物时收尾释放且同键可以重新加载()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();

            var attempts = 0;
            var dead = new FakeProduct("dead");
            loader.OnLoad = (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    // IsAlive 为假：框架必须撤下产物并收尾释放，而不是交付它。
                    return Task.FromResult(new LoadedAsset(dead, isAlive: () => false, releaseAsync: () =>
                    {
                        dead.Released = true;
                        return Task.CompletedTask;
                    }));
                }
                return Task.FromResult(new LoadedAsset(new FakeProduct("good")));
            };

            var error = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local).WaitAsync(TestTimeout));
            Assert.Equal(DiagnosticCodes.LoaderInvalidResult, error.Error.DiagnosticCode);
            Assert.True(dead.Released);
            Assert.Empty(manager.GetSnapshot().ResourceRows);

            using var reference = await manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local)
                .WaitAsync(TestTimeout);
            Assert.Equal("good", ReadValue(context, reference).Name);
        }

        [Fact]
        public async Task 释放失败的残留随结果返回且不封锁同键()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await manager.InitializeAsync();

            loader.OnLoad = (_, _) => Task.FromResult(new LoadedAsset(new FakeProduct("fragile"),
                releaseAsync: () => throw new InvalidOperationException("native release failed")));

            var reference = await manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local)
                .WaitAsync(TestTimeout);
            reference.Dispose();

            var result = await manager.UnloadUnusedAsync().WaitAsync(TestTimeout);
            var failure = Assert.Single(result.Failures);
            Assert.Equal(DiagnosticCodes.LifecycleReleaseFailed, failure.Error.DiagnosticCode);
            Assert.Equal(LoadStage.ReleaseAsset, failure.Error.Stage);
            Assert.NotEmpty(failure.RetainedObjects);
            Assert.Empty(manager.GetSnapshot().ResourceRows);

            // 残留移交后不永久封锁 key：同键可以重新加载。
            loader.OnLoad = null;
            using var again = await manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local)
                .WaitAsync(TestTimeout);
            Assert.Equal("asset", ReadValue(context, again).Name);
        }

        [Fact]
        public async Task 退出时释放已登记产物并使新请求失败()
        {
            using var context = new TestExecutionContext();
            var manager = NewManager(context, out _);
            await manager.InitializeAsync();
            var reference = await manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local).WaitAsync(TestTimeout);
            var product = ReadValue(context, reference);

            manager.Dispose();
            await V2Test.WaitUntilAsync(() => product.Released);

            Assert.True(manager.IsDisposed);
            Assert.True(product.Released);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local));
        }

        [Fact]
        public async Task 未初始化时加载被拒且不改变状态()
        {
            using var context = new TestExecutionContext();
            using var manager = NewManager(context, out var loader);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.LoadAsync<FakeProduct>("asset", ResourceSource.Local));

            Assert.False(manager.IsInitialized);
            Assert.Equal(0, loader.ResolveCount);
            Assert.Empty(manager.GetSnapshot().ResourceRows);
        }

        [Fact]
        public async Task 未注册来源按诊断码失败且之后可以补注册()
        {
            using var context = new TestExecutionContext();
            using var manager = new ResourceManager(context);
            await manager.InitializeAsync();

            var error = await Assert.ThrowsAsync<ResourceLoadException>(() =>
                manager.LoadAsync<FakeProduct>("asset", ResourceSource.Remote));
            Assert.Equal(DiagnosticCodes.LoaderNotRegistered, error.Error.DiagnosticCode);

            var loader = new FakeLoader();
            manager.RegisterLoader(ResourceSource.Remote, loader);
            using var reference = await manager.LoadAsync<FakeProduct>("asset", ResourceSource.Remote)
                .WaitAsync(TestTimeout);
            Assert.Equal(1, loader.LoadCount);
        }

        [Fact]
        public async Task 重复注册与已退出后的注册都被拒绝()
        {
            using var context = new TestExecutionContext();
            var manager = NewManager(context, out _);
            await manager.InitializeAsync();

            Assert.Throws<InvalidOperationException>(() =>
                manager.RegisterLoader(ResourceSource.Local, new FakeLoader()));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.InitializeAsync(_ => { }));

            manager.Dispose();
            Assert.Throws<ObjectDisposedException>(() =>
                manager.RegisterLoader(ResourceSource.Remote, new FakeLoader()));
            Assert.Throws<ObjectDisposedException>(() => { _ = manager.InitializeAsync(); });
        }
    }
}
