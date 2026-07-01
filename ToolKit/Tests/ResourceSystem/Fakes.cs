/*
 * 资源系统单元测试 —— 公共假实现 (xUnit, 纯 .NET, 不依赖 Unity)。
 */

using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class FakeAsset { public string Name = ""; }
    public sealed class FakePrefab { public string Name = ""; }
    public sealed class FakeInstance { public FakePrefab? Proto; public bool Active; public bool Destroyed; }
    public sealed class FakeTarget { public string Tag = ""; }

    /// <summary> 同步完成的加载器: 立即返回成功句柄, 记录加载次数。 </summary>
    public sealed class FakeLoader : ILoader
    {
        public int LoadCount;
        public Func<string, object> Factory = _ => new FakeAsset { Name = "x" };

        public ELoadType LoadType => ELoadType.Custom;
        public int MaxConcurrentLoads { get; set; }
        public bool CanLoad(string address) => true;

        public Task<IAssetHandle> LoadAsync(string address, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref LoadCount);
            var h = new AssetHandle(address);
            h.SetSucceed(Factory(address), null);
            return Task.FromResult<IAssetHandle>(h);
        }
    }

    /// <summary> 受控加载器: LoadAsync 挂起, 直到测试调用 Release(address) 才完成。用于测时序。 </summary>
    public sealed class GatedLoader : ILoader
    {
        private readonly Dictionary<string, TaskCompletionSource<bool>> _gates = new();
        private readonly object _lock = new();
        public Func<string, object> Factory = a => new FakeAsset { Name = a };
        public int LoadCount;
        public int EnteredCount;
        public int ActiveCount;
        public int MaxActiveCount;

        public ELoadType LoadType => ELoadType.Custom;
        public int MaxConcurrentLoads { get; set; }
        public bool CanLoad(string address) => true;

        private TaskCompletionSource<bool> Gate(string a)
        {
            lock (_lock)
            {
                if (!_gates.TryGetValue(a, out var t))
                {
                    t = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates[a] = t;
                }
                return t;
            }
        }

        public void Release(string address) => Gate(address).TrySetResult(true);

        public async Task<IAssetHandle> LoadAsync(string address, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref EnteredCount);
            var active = Interlocked.Increment(ref ActiveCount);
            UpdateMaxActive(active);
            try
            {
                await Gate(address).Task.ConfigureAwait(false);
                Interlocked.Increment(ref LoadCount);
                var h = new AssetHandle(address);
                h.SetSucceed(Factory(address), null);
                return h;
            }
            finally
            {
                Interlocked.Decrement(ref ActiveCount);
            }
        }

        private void UpdateMaxActive(int active)
        {
            while (true)
            {
                var current = MaxActiveCount;
                if (active <= current)
                {
                    return;
                }
                if (Interlocked.CompareExchange(ref MaxActiveCount, active, current) == current)
                {
                    return;
                }
            }
        }
    }

    /// <summary> 总是失败的加载器: 返回带指定错误码的失败句柄。 </summary>
    public sealed class FailingLoader : ILoader
    {
        public ELoadError Code = ELoadError.NotFound;
        public string Msg = "模拟失败";

        public ELoadType LoadType => ELoadType.Custom;
        public int MaxConcurrentLoads { get; set; }
        public bool CanLoad(string address) => true;

        public Task<IAssetHandle> LoadAsync(string address, CancellationToken cancellationToken = default)
        {
            var h = new AssetHandle(address);
            h.SetFailed(Code, Msg);
            return Task.FromResult<IAssetHandle>(h);
        }
    }

    /// <summary> 假实例提供者: 只把 FakePrefab 当成可实例化原型。 </summary>
    public sealed class FakeInstanceProvider : IInstanceProvider
    {
        public bool CanInstantiate(IAssetHandle prototype) => prototype.GetAsset<FakePrefab>() != null;
        public object Create(IAssetHandle prototype) => new FakeInstance { Proto = prototype.GetAsset<FakePrefab>() };
        public void OnGet(object instance) { if (instance is FakeInstance fi) fi.Active = true; }
        public void OnReturn(object instance) { if (instance is FakeInstance fi) fi.Active = false; }
        public void OnDestroy(object instance) { if (instance is FakeInstance fi) fi.Destroyed = true; }
        public bool IsAlive(object instance) => instance is FakeInstance fi && !fi.Destroyed;
    }

    /// <summary> 记录式 Applicable: 记下最后一次应用的目标与资源。 </summary>
    public sealed class CaptureApplicable : IApplicable
    {
        public int ApplyCount;
        public object? LastTarget;
        public object? LastResource;

        public void Apply<T, R>(T target, R resource) where T : class where R : class
        {
            ApplyCount++;
            LastTarget = target;
            LastResource = resource;
        }
    }
}
