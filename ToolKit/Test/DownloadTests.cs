using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ToolKit.Tools.NetTools;
using Xunit;

namespace ToolKit.Tests;

public abstract class MockableHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return SendAsyncPublic(request, cancellationToken);
    }

    public abstract Task<HttpResponseMessage> SendAsyncPublic(HttpRequestMessage request,
        CancellationToken cancellationToken);
}

public class DCFSDownloadTests
{
    private readonly Mock<MockableHttpMessageHandler> _httpMessageHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly DownloadConfig _downloadConfig;

    public DCFSDownloadTests()
    {
        _httpMessageHandlerMock = new Mock<MockableHttpMessageHandler> { CallBase = true };
        _httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        _downloadConfig = new DownloadConfig()
        {
            RemoteUrl = "http://example.com/file",
            LocalFilePath = "file",
            MaxRetries = 3,
            RequestTimeout = 10,
            WakeUpInterval = 5
        };
    }

    /// <summary>
    /// 高延迟，自动超时测试
    /// </summary>
    [Fact]
    public async Task Download_WithHighLatency_ShouldHandleTimeout()
    {
        var downloadService = new DCFSDownloader(_downloadConfig);
        downloadService.SetUpHttpClient(this._httpClient);
        var cancellationToken = new CancellationTokenSource(2000).Token; // 2 seconds timeout

        /*
         * _httpMessageHandlerMock 是使用 Moq 库创建的模拟对象，用于模拟 HttpMessageHandler 的行为。该模拟对象的目的是模拟 HTTP 请求中的高延迟。 下面是它的工作原理：
         * 设置模拟对象： 模拟对象被配置为拦截 SendAsyncPublic 方法的调用，该方法是 MockableHttpMessageHandler 类中的一个抽象方法。重载该方法是为了模拟 HttpMessageHandler 类中 SendAsync 方法的行为。
         * 模拟延迟： Setup 方法用于指定在调用 SendAsyncPublic 时应返回延迟的 HttpResponseMessage。使用 Task.Delay(5000) 方法引入延迟，模拟 5 秒钟的延迟。
         * 返回响应： 延迟后，会返回一个状态代码为 OK 的新 HttpResponseMessage。
         */
        _httpMessageHandlerMock
            .Setup(m => m.SendAsyncPublic(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async (HttpRequestMessage request, CancellationToken token) =>
            {
                await Task.Delay(5000); // Simulate high latency
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            });

        var result = await downloadService.Download(cancellationToken);

        Assert.Empty(result);
    }

    /// <summary>
    /// 高丢包，请求失败测试
    /// </summary>
    [Fact]
    public async Task Download_WithPacketLoss_ShouldHandleError()
    {
        var downloadService = new DCFSDownloader(_downloadConfig);
        downloadService.SetUpHttpClient(this._httpClient);
        var cancellationToken = CancellationToken.None;

        _httpMessageHandlerMock
            .Setup(m => m.SendAsyncPublic(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .Throws(new HttpRequestException("Simulated packet loss"));

        var result = await downloadService.Download(cancellationToken);

        Assert.Empty(result);
    }

    /// <summary>
    /// 无法连接，请求失败测试
    /// </summary>
    [Fact]
    public async Task Download_WhenServerUnreachable_ShouldHandleError()
    {
        var downloadService = new DCFSDownloader(_downloadConfig);
        downloadService.SetUpHttpClient(this._httpClient);
        var cancellationToken = CancellationToken.None;

        _httpMessageHandlerMock
            .Setup(m => m.SendAsyncPublic(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .Throws(new HttpRequestException("Server unreachable"));

        var result = await downloadService.Download(cancellationToken);

        Assert.Empty(result);
    }

    /// <summary>
    /// 断点续传测试
    /// </summary>
    [Fact]
    public async Task Download_WithPartialDownload_ShouldResumeDownload()
    {
        var downloadService = new DCFSDownloader(_downloadConfig);
        downloadService.SetUpHttpClient(this._httpClient);
        var cancellationToken = CancellationToken.None;

        _httpMessageHandlerMock
            .SetupSequence(m => m.SendAsyncPublic(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent))
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var result = await downloadService.Download(cancellationToken);

        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task Download_Success()
    {
        var downloadService = new DCFSDownloader(_downloadConfig);
        downloadService.SetUpHttpClient(this._httpClient);
        var cancellationToken = CancellationToken.None;

        _httpMessageHandlerMock
            .Setup(m => m.SendAsyncPublic(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var result = await downloadService.Download(cancellationToken);

        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task Download_RealData_Success()
    {
        var cfg = new DownloadConfig()
        {
            RemoteUrl = "http://172.16.126.185:4656/Apks/Test/Dev/2024-12-11/StormSquad_1.0.0.398_trunk_12519.apk",
            LocalFilePath = @"C:\Users\tongguangdong\Desktop\Temps\StormSquad_1.0.0.398_trunk_12519.apk",
            MaxRetries = 3,
            RequestTimeout = 10,
            WakeUpInterval = 5
        };
        var _fileSize = 250 * 1024 * 1024;
        var downloadService = new DCFSDownloader(cfg);
        try
        {
            var result = await downloadService.Download(CancellationToken.None);
            var fileName = System.Text.Encoding.UTF8.GetString(result);

            Assert.True(fileName == cfg.LocalFilePath, "下载的文件名不符合预期");
            Assert.True(File.Exists(cfg.LocalFilePath), "文件未下载成功");
            var fileInfo = new FileInfo(cfg.LocalFilePath);
            Assert.True(fileInfo.Length > _fileSize, "下载的文件大小不符合预期");
        }
        catch (Exception ex)
        {
            Assert.Fail($"下载过程中发生错误: {ex.Message}");
        }
        finally
        {
            // 清理工作：删除下载的文件
            if (File.Exists(cfg.LocalFilePath))
            {
                File.Delete(cfg.LocalFilePath);
            }
        }
    }
}

public class SimpleDownloadTests
{
    private readonly Mock<MockableHttpMessageHandler> _httpMessageHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly DownloadConfig _downloadConfig;

    public SimpleDownloadTests()
    {
        _httpMessageHandlerMock = new Mock<MockableHttpMessageHandler> { CallBase = true };
        _httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        _downloadConfig = new DownloadConfig()
        {
            RemoteUrl = "http://example.com/file",
            LocalFilePath = "file",
            MaxRetries = 3,
            RequestTimeout = 10,
            WakeUpInterval = 5
        };
    }

    /// <summary>
    /// 高延迟，自动超时测试
    /// </summary>
    [Fact]
    public async Task Download_WithHighLatency_ShouldHandleTimeout()
    {
        var downloadService = new SimpleDownloader(_downloadConfig);
        downloadService.SetUpHttpClient(this._httpClient);
        var cancellationToken = new CancellationTokenSource(2000).Token; // 2 seconds timeout

        /*
         * _httpMessageHandlerMock 是使用 Moq 库创建的模拟对象，用于模拟 HttpMessageHandler 的行为。该模拟对象的目的是模拟 HTTP 请求中的高延迟。 下面是它的工作原理：
         * 设置模拟对象： 模拟对象被配置为拦截 SendAsyncPublic 方法的调用，该方法是 MockableHttpMessageHandler 类中的一个抽象方法。重载该方法是为了模拟 HttpMessageHandler 类中 SendAsync 方法的行为。
         * 模拟延迟： Setup 方法用于指定在调用 SendAsyncPublic 时应返回延迟的 HttpResponseMessage。使用 Task.Delay(5000) 方法引入延迟，模拟 5 秒钟的延迟。
         * 返回响应： 延迟后，会返回一个状态代码为 OK 的新 HttpResponseMessage。
         */
        _httpMessageHandlerMock
            .Setup(m => m.SendAsyncPublic(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async (HttpRequestMessage request, CancellationToken token) =>
            {
                await Task.Delay(5000); // Simulate high latency
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            });

        var result = await downloadService.Download(cancellationToken);

        Assert.Empty(result);
    }

    /// <summary>
    /// 高丢包，请求失败测试
    /// </summary>
    [Fact]
    public async Task Download_WithPacketLoss_ShouldHandleError()
    {
        var downloadService = new SimpleDownloader(_downloadConfig);
        downloadService.SetUpHttpClient(this._httpClient);
        var cancellationToken = CancellationToken.None;

        _httpMessageHandlerMock
            .Setup(m => m.SendAsyncPublic(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .Throws(new HttpRequestException("Simulated packet loss"));

        var result = await downloadService.Download(cancellationToken);

        Assert.Empty(result);
    }

    /// <summary>
    /// 无法连接，请求失败测试
    /// </summary>
    [Fact]
    public async Task Download_WhenServerUnreachable_ShouldHandleError()
    {
        var downloadService = new SimpleDownloader(_downloadConfig);
        downloadService.SetUpHttpClient(this._httpClient);
        var cancellationToken = CancellationToken.None;

        _httpMessageHandlerMock
            .Setup(m => m.SendAsyncPublic(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .Throws(new HttpRequestException("Server unreachable"));

        var result = await downloadService.Download(cancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Download_Success()
    {
        var downloadService = new SimpleDownloader(_downloadConfig);
        downloadService.SetUpHttpClient(this._httpClient);
        var cancellationToken = CancellationToken.None;

        _httpMessageHandlerMock
            .Setup(m => m.SendAsyncPublic(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[100])
            });

        var result = await downloadService.Download(cancellationToken);

        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task Download_RealData_Success()
    {
        var cfg = new DownloadConfig()
        {
            RemoteUrl =
                "http://172.16.126.185:4656/Channels\\LocalTest22\\Android\\1.0.0\\FirstPackage\\PackageManifest_FirstPackage_1.0.0.259.json",
            MaxRetries = 3,
            RequestTimeout = 10,
            WakeUpInterval = 5
        };
        var _fileSize = 796 * 1024;
        var downloadService = new SimpleDownloader(cfg);
        try
        {
            var result = await downloadService.Download(CancellationToken.None);
            Assert.True(result.Length > _fileSize, "下载的文件大小不符合预期");
        }
        catch (Exception ex)
        {
            Assert.Fail($"下载过程中发生错误: {ex.Message}");
        }
    }

    [Fact]
    public async Task Download_CancellationRequested()
    {
        var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        var downloader = new SimpleDownloader(_downloadConfig);
        downloader.SetUpHttpClient(_httpClient);

        await Assert.ThrowsAsync<OperationCanceledException>(() => downloader.Download(cancellationTokenSource.Token));
    }

    [Fact]
    public async Task Download_HttpRequestException()
    {
        _httpMessageHandlerMock
            .Setup(m => m.SendAsyncPublic(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        var downloader = new SimpleDownloader(_downloadConfig);
        downloader.SetUpHttpClient(_httpClient);

        var result = await downloader.Download(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Download_TimeoutException()
    {
        _httpMessageHandlerMock
            .Setup(m => m.SendAsyncPublic(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(Timeout.Infinite);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            });

        var downloader = new SimpleDownloader(_downloadConfig);
        downloader.SetUpHttpClient(_httpClient);

        await Assert.ThrowsAsync<TimeoutException>(() => downloader.Download(CancellationToken.None));
    }
}