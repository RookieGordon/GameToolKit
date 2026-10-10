using System.Collections.Concurrent;

namespace ToolKit.Tests.ResourceSystem;

/// <summary> Keeps the test on its owning thread while it explicitly advances queued Unity work. </summary>
internal sealed class MainThreadTestPump : SynchronizationContext, IDisposable
{
    private readonly SynchronizationContext? _previous = Current;
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();

    public MainThreadTestPump() => SetSynchronizationContext(this);
    public int PendingCount => _pending.Count;
    public override void Post(SendOrPostCallback callback, object? state) => _pending.Enqueue((callback, state));

    public void Drain()
    {
        while (_pending.TryDequeue(out var item)) item.Callback(item.State);
    }

    public void WaitForPost() => Assert.True(
        SpinWait.SpinUntil(() => PendingCount > 0, TimeSpan.FromSeconds(5)),
        "The background operation did not post its completion.");

    public void RunUntil(Func<bool> completed) => Assert.True(
        SpinWait.SpinUntil(() => { Drain(); return completed(); }, TimeSpan.FromSeconds(5)),
        "The operation did not complete while the main thread was pumping.");

    public void Dispose() => SetSynchronizationContext(_previous);
}
