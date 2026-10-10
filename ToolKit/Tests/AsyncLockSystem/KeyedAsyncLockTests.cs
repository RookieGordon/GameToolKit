using ToolKit.Tools.Common;

namespace ToolKit.Tests.AsyncLockSystem;

public class KeyedAsyncLockTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SameKeyWaitsInOrder_WhileOtherKeysProceed()
    {
        var locks = new KeyedAsyncLock<int>();
        var first = await locks.LockAsync(1);
        var second = locks.LockAsync(1);
        var third = locks.LockAsync(1);
        using var otherKey = await locks.LockAsync(2).WaitAsync(Timeout);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        first.Dispose();
        var secondHolder = await second.WaitAsync(Timeout);
        Assert.False(third.IsCompleted);
        secondHolder.Dispose();
        using var thirdHolder = await third.WaitAsync(Timeout);
    }

    [Fact]
    public async Task FailureEndsCurrentWaitingBatch_NewRequestsWaitForCleanupThenRetry()
    {
        var locks = new KeyedAsyncLock<int>();
        var holder = await locks.LockAsync(1);
        var second = locks.LockAsync(1);
        var third = locks.LockAsync(1);
        var failure = new InvalidOperationException("local resource is broken");

        holder.FailWaitingRequests(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await second.WaitAsync(Timeout)));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await third.WaitAsync(Timeout)));
        var nextRound = locks.LockAsync(1);
        Assert.False(nextRound.IsCompleted);
        holder.Dispose();
        using var nextHolder = await nextRound.WaitAsync(Timeout);
    }

    [Fact]
    public async Task CancelledWaiterLeavesQueue_WithoutAffectingOtherRequests()
    {
        var locks = new KeyedAsyncLock<int>();
        var holder = await locks.LockAsync(1);
        using var cancellation = new CancellationTokenSource();
        var cancelled = locks.LockAsync(1, cancellation.Token);
        var following = locks.LockAsync(1);

        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelled.WaitAsync(Timeout));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(following.IsCompleted);
        holder.Dispose();
        using var followingHolder = await following.WaitAsync(Timeout);
    }

    [Fact]
    public async Task AlreadyCancelledRequest_DoesNotTakeExecutionRight()
    {
        var locks = new KeyedAsyncLock<int>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await locks.LockAsync(1, cancellation.Token));

        using var holder = await locks.LockAsync(1).WaitAsync(Timeout);
    }

    [Fact]
    public async Task CancellingAfterAcquisition_DoesNotReleaseExecutionRight()
    {
        var locks = new KeyedAsyncLock<int>();
        using var cancellation = new CancellationTokenSource();
        var holder = await locks.LockAsync(1, cancellation.Token);
        cancellation.Cancel();
        var following = locks.LockAsync(1);
        Assert.False(following.IsCompleted);
        holder.Dispose();
        using var followingHolder = await following.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ReleasingOldHandleTwice_DoesNotReleaseNextHolderOrNewEntry()
    {
        var locks = new KeyedAsyncLock<int>();
        var first = await locks.LockAsync(1);
        var second = locks.LockAsync(1);
        first.Dispose();
        var secondHolder = await second.WaitAsync(Timeout);
        var third = locks.LockAsync(1);

        first.Dispose();
        first.FailWaitingRequests(new InvalidOperationException("stale failure"));
        Assert.False(third.IsCompleted);
        secondHolder.Dispose();
        var thirdHolder = await third.WaitAsync(Timeout);
        thirdHolder.Dispose();

        var newHolder = await locks.LockAsync(1);
        var newWaiter = locks.LockAsync(1);
        thirdHolder.Dispose();
        first.Dispose();
        Assert.False(newWaiter.IsCompleted);
        newHolder.Dispose();
        using var newWaiterHolder = await newWaiter.WaitAsync(Timeout);
    }

    [Fact]
    public async Task StringCompatibilityHandle_CopiesReleaseOnlyOnce()
    {
        var locks = new KeyedAsyncLock();
        var first = await locks.LockAsync("resource");
        var copy = first;
        first.Dispose();
        var second = await locks.LockAsync("resource");
        var third = locks.LockAsync("resource");

        copy.Dispose();
        Assert.False(third.IsCompleted);
        second.Dispose();
        using var thirdHolder = await third.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ConcurrentOperations_NeverEnterSameKeyTogether()
    {
        var locks = new KeyedAsyncLock<int>();
        var active = 0;
        var overlap = 0;
        var completed = 0;
        var jobs = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            using (await locks.LockAsync(1))
            {
                if (Interlocked.Increment(ref active) != 1)
                    Interlocked.Increment(ref overlap);
                await Task.Yield();
                Interlocked.Increment(ref completed);
                Interlocked.Decrement(ref active);
            }
        }));

        await Task.WhenAll(jobs).WaitAsync(Timeout);

        Assert.Equal(0, overlap);
        Assert.Equal(100, completed);
    }
}
