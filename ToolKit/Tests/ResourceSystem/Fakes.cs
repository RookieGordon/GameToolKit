/*
 * 资源系统 V2 单元测试 —— 公共假实现与工具 (xUnit, 纯 .NET, 不依赖 Unity)。
 * 竞态通过 TCS 栅栏控制先后，不用真实 Sleep 碰运气；所有计数在排空 (ShutdownAsync) 后断言。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class FakeAsset
    {
        public readonly string Name;
        public FakeAsset(string name) { Name = name; }
    }

    public sealed class FakePrefab
    {
        public readonly string Name;
        public FakePrefab(string name) { Name = name; }
    }

    public sealed class FakeInstance
    {
        public readonly FakePrefab Proto;
        public bool Destroyed;
        public bool Rented;

        public FakeInstance(FakePrefab proto) { Proto = proto; }
    }

    /// <summary> 可手动推进的单调时钟 (秒) </summary>
    public sealed class ManualClock
    {
        private readonly object _gate = new object();
        private double _now;

        public double Now
        {
            get { lock (_gate) { return _now; } }
        }

        public void Advance(double seconds)
        {
            lock (_gate) { _now += seconds; }
        }

        public static implicit operator Func<double>(ManualClock clock)
        {
            return () => clock.Now;
        }
    }

    /// <summary> 收集最终失败诊断与过程事件的收集器 </summary>
    public sealed class DiagnosticCollector : IResourceDiagnostics
    {
        private readonly object _gate = new object();
        private readonly List<LoadError> _reports = new List<LoadError>();
        private readonly List<ResourceEvent> _traces = new List<ResourceEvent>();

        public void Report(LoadError error)
        {
            lock (_gate) { _reports.Add(error); }
        }

        public void Trace(ResourceEvent traceEvent)
        {
            lock (_gate) { _traces.Add(traceEvent); }
        }

        public int ReportCount => _reports.Count;

        public int CountByCode(string code)
        {
            lock (_gate) { return _reports.FindAll(r => r.DiagnosticCode == code).Count; }
        }

        public List<LoadError> SnapshotReports()
        {
            lock (_gate) { return new List<LoadError>(_reports); }
        }
    }

    /// <summary> 同步完成的加载器：立即返回成功结果，记录加载/释放次数 </summary>
    public sealed class FakeLoader : IResourceLoader
    {
        public int ResolveCount;
        public int LoadCount;
        public int ReleaseCount;
        public Func<string, object> Factory = a => new FakeAsset(a);
        public Func<string, string>? KeyOf;              // 地址 → LocalKey (版本模拟)
        public Queue<Exception>? LoadErrors;             // 按序抛出的异常
        public Queue<LoadedAsset?>? ResultOverrides;     // 按序返回的结果 (null = 无结果)

        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ResolveCount);
            var key = KeyOf != null ? KeyOf(request.Address) : request.Address;
            return Task.FromResult(new ResolvedResource(key, typeof(object), request.Address));
        }

        public Task<LoadedAsset> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            Interlocked.Increment(ref LoadCount);
            if (LoadErrors is { Count: > 0 })
            {
                throw LoadErrors.Dequeue();
            }
            if (ResultOverrides is { Count: > 0 })
            {
                var overrideResult = ResultOverrides.Dequeue();
                return Task.FromResult(overrideResult ?? null!);
            }
            var value = Factory(resource.LocalKey);
            var loaded = new LoadedAsset(value, 100, null, () =>
            {
                Interlocked.Increment(ref ReleaseCount);
                return Task.CompletedTask;
            });
            return Task.FromResult(loaded);
        }
    }

    /// <summary> 受控加载器：LoadAsync 挂起直到测试放行栅栏；可选忽略取消令牌以模拟迟到结果 </summary>
    public sealed class GatedLoader : IResourceLoader
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _gates =
            new Dictionary<string, TaskCompletionSource<bool>>();

        public int ResolveCount;
        public int LoadCount;
        public int ReleaseCount;
        public int ActiveCount;
        public int MaxActiveCount;
        public bool IgnoreCancellation;
        public Func<string, object> Factory = a => new FakeAsset(a);

        /// <summary> 非空时放行栅栏后抛出该异常而不是返回结果 (共享失败模拟) </summary>
        public Exception? FailWith;

        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ResolveCount);
            return Task.FromResult(new ResolvedResource(request.Address, typeof(object), request.Address));
        }

        public async Task<LoadedAsset> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            Interlocked.Increment(ref LoadCount);
            var active = Interlocked.Increment(ref ActiveCount);
            int max;
            do
            {
                max = Volatile.Read(ref MaxActiveCount);
                if (active <= max)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref MaxActiveCount, active, max) != max);
            try
            {
                return await _LoadGatedAsync(resource, progress, operationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref ActiveCount);
            }
        }

        private async Task<LoadedAsset> _LoadGatedAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            var gate = Gate(resource.LocalKey);
            if (IgnoreCancellation)
            {
                await gate.Task.ConfigureAwait(false);
            }
            else
            {
                await gate.Task.WaitTestCancellation(operationToken).ConfigureAwait(false);
            }
            if (FailWith != null)
            {
                throw FailWith;
            }
            var value = Factory(resource.LocalKey);
            return new LoadedAsset(value, 100, null, () =>
            {
                Interlocked.Increment(ref ReleaseCount);
                return Task.CompletedTask;
            });
        }

        public void ReleaseGate(string key)
        {
            Gate(key).TrySetResult(true);
        }

        private TaskCompletionSource<bool> Gate(string key)
        {
            lock (_gate)
            {
                if (!_gates.TryGetValue(key, out var tcs))
                {
                    tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates[key] = tcs;
                }
                return tcs;
            }
        }
    }

    /// <summary> 可注入行为的实例工厂：记录创建/重置/销毁次数 </summary>
    public sealed class FakeInstanceFactory : IInstanceFactory
    {
        public int CreateCount;
        public int DestroyCount;
        public int OnRentCount;
        public int OnReturnCount;
        public ResourceRequest? LastCreationRequest;

        public Exception? ThrowOnCreate;      // ResourceLoadException 携带清理语义
        public Exception? ThrowOnCanCreate;
        public Exception? ThrowOnRent;
        public Exception? ThrowOnReturn;
        public Exception? ThrowOnDestroy;

        /// <summary> 销毁确认门：非空时 DestroyAsync 等待其完成 (P08 两阶段销毁) </summary>
        public TaskCompletionSource<bool>? DestroyGate;

        public ResourceRequest GetPrototypeRequest(ResourceRequest instanceRequest)
        {
            return instanceRequest; // 原型请求与实例请求一致
        }

        public string GetInstanceKey(ResourceRequest instanceRequest)
        {
            return instanceRequest.Parameters as string ?? "default";
        }

        public bool CanCreate(object prototype, Type instanceType)
        {
            if (ThrowOnCanCreate != null)
            {
                throw ThrowOnCanCreate;
            }
            return prototype is FakePrefab;
        }

        public Task<object> CreateAsync(object prototype, ResourceRequest creationRequest, CancellationToken operationToken)
        {
            Interlocked.Increment(ref CreateCount);
            LastCreationRequest = creationRequest;
            if (ThrowOnCreate != null)
            {
                throw ThrowOnCreate;
            }
            return Task.FromResult<object>(new FakeInstance((FakePrefab)prototype));
        }

        public bool IsAlive(object instance)
        {
            return instance is FakeInstance { Destroyed: false };
        }

        public void OnRent(object instance)
        {
            Interlocked.Increment(ref OnRentCount);
            if (ThrowOnRent != null)
            {
                throw ThrowOnRent;
            }
            ((FakeInstance)instance).Rented = true;
        }

        public void OnReturn(object instance)
        {
            Interlocked.Increment(ref OnReturnCount);
            if (ThrowOnReturn != null)
            {
                throw ThrowOnReturn;
            }
            ((FakeInstance)instance).Rented = false;
        }

        public async Task DestroyAsync(object instance)
        {
            if (DestroyGate != null)
            {
                await DestroyGate.Task.ConfigureAwait(false);
            }
            if (ThrowOnDestroy != null)
            {
                throw ThrowOnDestroy;
            }
            Interlocked.Increment(ref DestroyCount);
            ((FakeInstance)instance).Destroyed = true;
        }
    }

    public static class V2Test
    {
        public static ResourceManager NewManager(
            out ManualClock clock,
            out DiagnosticCollector diagnostics,
            Action<ResourceSystemOptions>? configure = null)
        {
            clock = new ManualClock();
            diagnostics = new DiagnosticCollector();
            var options = new ResourceSystemOptions { RequestTimeout = null, DefaultLoader = "fake" };
            configure?.Invoke(options);
            return new ResourceManager(ImmediateExecutionContext.Instance, options, null, diagnostics, clock);
        }

        public static async Task<Exception> Capture<T>(Task<T> task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ex;
            }
            return null!;
        }
    }

    internal static class TestAsyncExtensions
    {
        /// <summary> 可被取消中断的等待：令牌触发时抛 OCE，不取消被等待的操作 </summary>
        public static async Task WaitTestCancellation(this Task task, CancellationToken cancellationToken)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                var done = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
                if (ReferenceEquals(done, cancelled.Task))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            await task.ConfigureAwait(false);
        }
    }
}
