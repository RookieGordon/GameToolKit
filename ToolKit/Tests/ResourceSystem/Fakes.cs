#nullable disable
using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem;

/// <summary> 可控加载器：每次 LoadAsync 登记一个 PendingLoad，由测试决定何时成功/失败；默认立即成功 </summary>
public sealed class FakeLoader : ILoader
{
    private readonly object _gate = new();
    private readonly List<PendingLoad> _pending = new();
    private int _loadCount;

    public bool AutoComplete { get; set; } = true;
    /// <summary> 像真实加载器一样观察本轮取消令牌；关掉它来模拟"后端停不下来"的迟到结果 </summary>
    public bool ObserveRoundCancel { get; set; } = true;
    public Func<ResourceKey, object> Produce { get; set; } = key => "product:" + key.Address;
    public List<FakeProduct> Products { get; } = new();
    public int LoadCount => Volatile.Read(ref _loadCount);
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    public Task<IResource> LoadAsync(ResourceKey key, IProgress<OperationProgress> progress, CancellationToken cancellation)
    {
        Interlocked.Increment(ref _loadCount);
        var pending = new PendingLoad(this, key, progress, cancellation);
        lock (_gate) _pending.Add(pending);
        if (AutoComplete) pending.Complete();
        return pending.Task;
    }

    public PendingLoad Pending(int index)
    {
        lock (_gate) return _pending[index];
    }

    public async Task<PendingLoad> WaitForPendingAsync(int index)
    {
        await TestClock.WaitUntilAsync(() => PendingCount > index);
        return Pending(index);
    }

    internal FakeProduct Make(ResourceKey key, object value)
    {
        var product = new FakeProduct(value ?? Produce(key));
        lock (_gate) Products.Add(product);
        return product;
    }
}

public sealed class PendingLoad
{
    private readonly FakeLoader _owner;
    private readonly IProgress<OperationProgress> _progress;
    private readonly TaskCompletionSource<IResource> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingLoad(FakeLoader owner, ResourceKey key, IProgress<OperationProgress> progress, CancellationToken roundToken)
    {
        _owner = owner;
        _progress = progress;
        Key = key;
        RoundToken = roundToken;
        if (owner.ObserveRoundCancel)
            roundToken.Register(() => _completion.TrySetCanceled(roundToken));
    }

    public ResourceKey Key { get; }
    /// <summary> 这一轮实际加载的取消令牌 (最后一个等待者退出时触发) </summary>
    public CancellationToken RoundToken { get; }
    public Task<IResource> Task => _completion.Task;

    public FakeProduct Complete(object value = null)
    {
        var product = _owner.Make(Key, value);
        _completion.TrySetResult(product);
        return product;
    }

    public void Fail(Exception error) => _completion.TrySetException(error);
    public void CancelByRound() => _completion.TrySetCanceled(RoundToken);
    public void Report(double fraction, string message = null) => _progress?.Report(new OperationProgress(fraction, message));
}

public sealed class FakeProduct : IResource
{
    private int _released;

    public FakeProduct(object value) => Value = value;

    public object Value { get; }
    public int Released => Volatile.Read(ref _released);
    /// <summary> 设置后 Release 抛出该异常 (计数仍增加) </summary>
    public Exception ReleaseError { get; set; }

    public void Release()
    {
        Interlocked.Increment(ref _released);
        if (ReleaseError != null) throw ReleaseError;
    }
}

/// <summary> 委托转换器：输出 FakeProduct，便于观察释放 </summary>
public sealed class FuncConverter : IProductConverter
{
    private readonly Func<object, object> _convert;

    public FuncConverter(Func<object, object> convert) => _convert = convert;

    public List<FakeProduct> Outputs { get; } = new();

    public Task<IResource> ConvertAsync(IResource source, ResourceKey key, CancellationToken cancellation)
    {
        var output = new FakeProduct(_convert(source.Value));
        lock (Outputs) Outputs.Add(output);
        return System.Threading.Tasks.Task.FromResult<IResource>(output);
    }
}

/// <summary> 假实例工厂：原型是 string，实例是 FakeInstance；可注入创建/重置/销毁失败 </summary>
public sealed class FakeFactory : IInstanceFactory
{
    private int _destroyed;

    public Type PrototypeType => typeof(string);
    public List<FakeInstance> Created { get; } = new();
    public int Destroyed => Volatile.Read(ref _destroyed);
    public Exception CreateError { get; set; }
    public Exception RentError { get; set; }
    public Exception ReturnError { get; set; }
    public HashSet<object> FailDestroy { get; } = new();
    /// <summary> 创建完成前的钩子 (用于在创建中触发取消) </summary>
    public Action OnCreate { get; set; }

    public object Create(object prototype)
    {
        OnCreate?.Invoke();
        if (CreateError != null) throw CreateError;
        var instance = new FakeInstance(prototype);
        lock (Created) Created.Add(instance);
        return instance;
    }

    public void OnRent(object instance)
    {
        ((FakeInstance)instance).Rents++;
        if (RentError != null) throw RentError;
    }

    public void OnReturn(object instance)
    {
        ((FakeInstance)instance).Returns++;
        if (ReturnError != null) throw ReturnError;
    }

    public void Destroy(object instance)
    {
        lock (FailDestroy)
        {
            if (FailDestroy.Contains(instance)) throw new InvalidOperationException("销毁失败");
        }
        ((FakeInstance)instance).Destroyed = true;
        Interlocked.Increment(ref _destroyed);
    }
}

public sealed class FakeInstance
{
    public FakeInstance(object prototype) => Prototype = prototype;
    public object Prototype { get; }
    public int Rents { get; set; }
    public int Returns { get; set; }
    public bool Destroyed { get; set; }
}

public static class TestClock
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("等待条件超时");
            await System.Threading.Tasks.Task.Delay(5);
        }
    }
}

public static class TestSystem
{
    public static ResourceSystemOptions Options(FakeLoader loader, LoaderPolicy? policy = null)
    {
        return new ResourceSystemOptions().AddLoader(ResourceSource.Local, loader, policy ?? new LoaderPolicy(4, null));
    }

    public static async Task<ToolKit.Tools.Common.ResourceSystem> CreateAsync(FakeLoader loader, LoaderPolicy? policy = null,
        Action<ResourceSystemOptions> configure = null)
    {
        var options = Options(loader, policy);
        configure?.Invoke(options);
        var system = new ToolKit.Tools.Common.ResourceSystem(options);
        await system.InitializeAsync();
        return system;
    }
}
