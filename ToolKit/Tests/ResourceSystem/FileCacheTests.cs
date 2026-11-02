#nullable disable
using System.Text;
using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem;

/// <summary> 可控的取得实现：登记每次填充，由测试决定何时写文件完成或失败 </summary>
public sealed class FakeFetch
{
    private readonly object _gate = new();
    private readonly List<PendingFetch> _pending = new();

    public bool AutoComplete { get; set; } = true;
    public bool ObserveCancel { get; set; } = true;
    public byte[] Content { get; set; } = Encoding.ASCII.GetBytes("remote-content");
    public int Count { get { lock (_gate) return _pending.Count; } }

    public FileCache.Fetch Delegate => (url, temporaryPath, progress, token) =>
    {
        var pending = new PendingFetch(url, temporaryPath, token, ObserveCancel);
        lock (_gate) _pending.Add(pending);
        if (AutoComplete) pending.Complete(Content);
        return pending.Task;
    };

    public PendingFetch Pending(int index)
    {
        lock (_gate) return _pending[index];
    }

    public async Task<PendingFetch> WaitForPendingAsync(int index)
    {
        await TestClock.WaitUntilAsync(() => Count > index);
        return Pending(index);
    }
}

public sealed class PendingFetch
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingFetch(string url, string temporaryPath, CancellationToken token, bool observeCancel)
    {
        Url = url;
        TemporaryPath = temporaryPath;
        Token = token;
        if (observeCancel) token.Register(() => _completion.TrySetCanceled(token));
    }

    public string Url { get; }
    public string TemporaryPath { get; }
    public CancellationToken Token { get; }
    public Task Task => _completion.Task;

    public void Complete(byte[] content)
    {
        File.WriteAllBytes(TemporaryPath, content);
        _completion.TrySetResult(true);
    }

    public void Fail(Exception error) => _completion.TrySetException(error);
}

public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* 测试残留不影响结果 */ }
    }
}

public class FileCacheTests
{
    private static readonly TimeSpan Timeout = TestClock.Timeout;

    private static FileCache Create(TempDirectory directory, FakeFetch fetch, TimeSpan? maxAge = null, long target = long.MaxValue)
    {
        return new FileCache(new HashedPathConverter(directory.Path), fetch.Delegate, target, maxAge);
    }

    [Fact]
    public async Task FirstGetFillsAndPublishes_SecondGetHitsWithoutFetch()
    {
        using var directory = new TempDirectory();
        var fetch = new FakeFetch();
        var cache = Create(directory, fetch);

        var path = await cache.GetFileAsync("http://h/a.bin", null, CancellationToken.None);
        Assert.Equal("remote-content", File.ReadAllText(path));
        Assert.EndsWith(".bin", path);
        Assert.StartsWith(directory.Path, path);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        Assert.True((DateTime.UtcNow - File.GetLastWriteTimeUtc(path)) < TimeSpan.FromSeconds(5));

        var again = await cache.GetFileAsync("http://h/a.bin", null, CancellationToken.None);
        Assert.Equal(path, again);
        Assert.Equal(1, fetch.Count);                                    // 命中缓存：不再取得
    }

    [Fact]
    public async Task ExpiredFileIsRefetched_FreshFileIsNot()
    {
        using var directory = new TempDirectory();
        var fetch = new FakeFetch();
        var cache = Create(directory, fetch, maxAge: TimeSpan.FromMinutes(10));

        var path = await cache.GetFileAsync("http://h/a.bin", null, CancellationToken.None);
        await cache.GetFileAsync("http://h/a.bin", null, CancellationToken.None);
        Assert.Equal(1, fetch.Count);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(11)); // 模拟到期
        fetch.Content = Encoding.ASCII.GetBytes("newer");
        var refreshed = await cache.GetFileAsync("http://h/a.bin", null, CancellationToken.None);
        Assert.Equal(path, refreshed);
        Assert.Equal(2, fetch.Count);
        Assert.Equal("newer", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task FailedFetchLeavesNoTemporaryFile_AndIsReportedWithUrl()
    {
        using var directory = new TempDirectory();
        var fetch = new FakeFetch { AutoComplete = false };
        var cache = Create(directory, fetch);

        var get = cache.GetFileAsync("http://h/a.bin", null, CancellationToken.None);
        var pending = await fetch.WaitForPendingAsync(0);
        File.WriteAllText(pending.TemporaryPath, "half");                // 写了一半再失败
        pending.Fail(new IOException("连接中断"));

        var error = await Assert.ThrowsAsync<ResourceException>(() => get.WaitAsync(Timeout));
        Assert.Equal(DiagnosticCodes.FileIoFailed, error.ResourceError.DiagnosticCode);
        Assert.Equal("http://h/a.bin", error.ResourceError.Context["url"]);
        Assert.Empty(Directory.GetFiles(directory.Path));                // 临时文件删除、不发布
    }

    [Fact]
    public async Task LastWaiterLeaving_CancelsFill_LateSuccessIsNotPublished_NextRequestStartsNewFill()
    {
        using var directory = new TempDirectory();
        var fetch = new FakeFetch { AutoComplete = false, ObserveCancel = false };
        var cache = Create(directory, fetch);
        using var cancel = new CancellationTokenSource();

        var first = cache.GetFileAsync("http://h/a.bin", null, cancel.Token);
        var pending = await fetch.WaitForPendingAsync(0);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Timeout));
        await TestClock.WaitUntilAsync(() => pending.Token.IsCancellationRequested);

        var second = cache.GetFileAsync("http://h/a.bin", null, CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal(1, fetch.Count);                                    // 新请求等旧填充真正退出
        pending.Complete(Encoding.ASCII.GetBytes("late"));              // 被放弃的填充迟到成功
        var next = await fetch.WaitForPendingAsync(1);                   // 旧轮退出后才开新一轮
        Assert.False(File.Exists(Path.ChangeExtension(pending.TemporaryPath, null)));
        next.Complete(Encoding.ASCII.GetBytes("fresh"));

        var path = await second.WaitAsync(Timeout);
        Assert.Equal("fresh", File.ReadAllText(path));                   // 迟到成功没有变成缓存
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task DifferentRepresentationsShareOneDownload_CancelingFirstDoesNotStopOthers()
    {
        using var directory = new TempDirectory();
        var fetch = new FakeFetch { AutoComplete = false };
        var cache = Create(directory, fetch);
        var options = new ResourceSystemOptions()
            .AddLoader(ResourceSource.Remote, new RemoteFileLoader(cache), new LoaderPolicy(4, null))
            .AddConverter<string>(new TextConverter());
        await using var system = new ToolKit.Tools.Common.ResourceSystem(options);
        await system.InitializeAsync();
        using var cancelBytes = new CancellationTokenSource();

        var bytes = system.LoadAsync<byte[]>(ResourceSource.Remote, "http://h/a.txt", token: cancelBytes.Token);
        var text = system.LoadAsync<string>(ResourceSource.Remote, "http://h/a.txt");
        var pending = await fetch.WaitForPendingAsync(0);
        await Task.Delay(50);
        Assert.Equal(1, fetch.Count);                                    // 两种表示共享一次下载

        cancelBytes.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bytes.WaitAsync(Timeout));
        Assert.False(pending.Token.IsCancellationRequested);             // 先到者取消不终止别人的下载

        pending.Complete(Encoding.UTF8.GetBytes("hello"));
        using var textRef = await text.WaitAsync(Timeout);
        Assert.Equal("hello", textRef.Value);
        Assert.Equal(1, fetch.Count);
    }

    [Fact]
    public async Task StartupCleanupDeletesTemporaryFiles_ThenOldestUntilUnderTarget()
    {
        using var directory = new TempDirectory();
        void Put(string name, int size, int ageMinutes)
        {
            var path = Path.Combine(directory.Path, name);
            File.WriteAllBytes(path, new byte[size]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(ageMinutes));
        }
        Put("old.bin", 100, 30);
        Put("mid.bin", 100, 20);
        Put("new.bin", 100, 10);
        Put("broken.abc.tmp", 1000, 0);

        var cache = Create(directory, new FakeFetch(), target: 250);
        await cache.InitializeAsync();

        var remaining = Directory.GetFiles(directory.Path).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "mid.bin", "new.bin" }, remaining);          // 临时文件先删；再删最旧的直到 ≤ 目标
    }

    [Fact]
    public void CacheWithoutValidStartupTargetIsRejected()
    {
        using var directory = new TempDirectory();
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(directory, new FakeFetch(), target: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(directory, new FakeFetch(), target: -1));
    }

    [Fact]
    public async Task LocalFileLoaderReadsBytes_ConvertsText_ReportsMissingFile()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "a.txt"), "本地文本");
        var options = new ResourceSystemOptions()
            .AddLoader(ResourceSource.Local, new LocalFileLoader(directory.Path), new LoaderPolicy(2, null))
            .AddConverter<string>(new TextConverter());
        await using var system = new ToolKit.Tools.Common.ResourceSystem(options);
        await system.InitializeAsync();

        using var bytes = await system.LoadAsync<byte[]>(ResourceSource.Local, "a.txt");
        using var text = await system.LoadAsync<string>(ResourceSource.Local, "a.txt");
        Assert.Equal("本地文本", Encoding.UTF8.GetString(bytes.Value));
        Assert.Equal("本地文本", text.Value);

        var missing = await Assert.ThrowsAsync<ResourceException>(() => system.LoadAsync<byte[]>(ResourceSource.Local, "missing.txt"));
        Assert.Equal(DiagnosticCodes.FileNotFound, missing.ResourceError.DiagnosticCode);
    }

    [Fact]
    public void HashedPathConverterIsStableAndKeepsExtension()
    {
        using var directory = new TempDirectory();
        var converter = new HashedPathConverter(directory.Path);
        var a = converter.RemoteUrlToLocalPath("http://h/x/a.png?v=1");
        Assert.Equal(a, converter.RemoteUrlToLocalPath("http://h/x/a.png?v=1"));
        Assert.NotEqual(a, converter.RemoteUrlToLocalPath("http://h/x/a.png?v=2"));
        Assert.EndsWith(".png", a);
        Assert.Equal(directory.Path, Path.GetDirectoryName(a));
    }
}
