using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.NetTools;

public enum EDownloadStatus
{
    Start,
    Downloading,
    Retry,
    Succeed,
    Failed,
}

public struct DownloadInfo
{
    public ulong CurrentSize;
    public float Progress;
    public string ErrorMsg;
    public string StrResult;
    public byte[] ByteResult;
}

public struct DownloadConfig
{
    public Action<EDownloadStatus, DownloadInfo> DownloadCallback;
    public string RemoteUrl;
    public string LocalFilePath;
    public int MaxRetries;
    /// <summary>
    /// 请求延迟，单位：秒
    /// </summary>
    public int RequestTimeout;
    /// <summary>
    /// 重试间隔，单位：秒
    /// </summary>
    public int WakeUpInterval;
}

public abstract class Downloader
{
    private static int _idCounter = 0;
    public int Id { get; set; }
    protected DownloadConfig Cfg { get; set; }

    protected CancellationToken _cancelToken;

    protected int _retryCount = 0;

    protected HttpClient _httpClient = null;

    public Downloader(DownloadConfig cfg)
    {
        Id = _idCounter++;
        Cfg = cfg;
    }

    public abstract Task<byte[]> Download(CancellationToken cancelToken);
    
    public virtual void SetUpHttpClient(HttpClient client)
    {
        this._httpClient = client;
    }

    public async Task<byte[]> WaitForRetry()
    {
        await Task.Delay(TimeSpan.FromSeconds(Cfg.WakeUpInterval), this._cancelToken);
        Console.WriteLine($"Retry count: {this._retryCount}");
        Cfg.DownloadCallback?.Invoke(EDownloadStatus.Retry, new DownloadInfo());
        return await Download(this._cancelToken);
    }

    protected async Task<byte[]> _OnDownloadError(string errorMsg)
    {
        if (this._retryCount > Cfg.MaxRetries)
        {
            Console.WriteLine($"Download failed. Error: {errorMsg}");
            Cfg.DownloadCallback?.Invoke(EDownloadStatus.Failed, new DownloadInfo()
            {
                ErrorMsg = errorMsg,
            });
            return Array.Empty<byte>();
        }

        Console.WriteLine($"Download failed, waiting for retry. Error:{errorMsg}");
        this._retryCount++;
        return await WaitForRetry();
    }
}

public class DCFSDownloader : Downloader
{
    private const int BufferSize = 2048;

    private string _tempFilePath;

    public DCFSDownloader(DownloadConfig cfg) : base(cfg)
    {
        if (string.IsNullOrEmpty(cfg.LocalFilePath))
        {
            throw new ArgumentException("LocalFilePath cannot be null or empty.");
        }

        this._tempFilePath = cfg.LocalFilePath + ".tmp";
    }

    public override async Task<byte[]> Download(CancellationToken cancelToken)
    {
        this._cancelToken = cancelToken;
        cancelToken.ThrowIfCancellationRequested();
        try
        {
            if (this._httpClient == null)
            {
                this._httpClient = new HttpClient();
            }
            using (this._httpClient)
            {
                if (Cfg.RequestTimeout > 0)
                {
                    this._httpClient.Timeout = TimeSpan.FromSeconds(Cfg.RequestTimeout);
                }

                await using (var fileStream = new FileStream(
                                 this._tempFilePath, FileMode.OpenOrCreate, FileAccess.Write,
                                 FileShare.Write, BufferSize, true))
                {
                    if (fileStream.Length > 0)
                    {
                        this._httpClient.DefaultRequestHeaders.Range =
                            new System.Net.Http.Headers.RangeHeaderValue(fileStream.Length, null);
                    }

                    HttpResponseMessage response = await this._httpClient.GetAsync(Cfg.RemoteUrl,
                        HttpCompletionOption.ResponseHeadersRead, cancelToken);

                    response.EnsureSuccessStatusCode();

                    long totalBytes = response.Content.Headers.ContentLength ?? 0;
                    long bytesRead = 0;
                    await using (var contentStream = await response.Content.ReadAsStreamAsync(cancelToken))
                    {
                        byte[] buffer = new byte[BufferSize];
                        int bytes;
                        while (true)
                        {
                            var readTask = contentStream.ReadAsync(buffer, 0, buffer.Length, cancelToken);
                            if (await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(5), cancelToken)) ==
                                readTask)
                            {
                                bytes = await readTask;
                                if (bytes == 0)
                                {
                                    break;
                                }

                                cancelToken.ThrowIfCancellationRequested();

                                await fileStream.WriteAsync(buffer, 0, bytes, cancelToken);
                                bytesRead += bytes;
                                Cfg.DownloadCallback?.Invoke(EDownloadStatus.Downloading, new DownloadInfo()
                                {
                                    CurrentSize = (ulong)bytesRead,
                                    Progress = (float)bytesRead / totalBytes,
                                });

                                cancelToken.ThrowIfCancellationRequested();
                            }
                            else
                            {
                                throw new TimeoutException("Timeout while reading from stream.");
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine("Operation was cancelled.");
            return Array.Empty<byte>();
        }
        catch (HttpRequestException ex)
        {
            return await _OnDownloadError(ex.ToString());
        }
        catch (Exception ex)
        {
            return await _OnDownloadError(ex.ToString());
        }

        try
        {
            if (File.Exists(Cfg.LocalFilePath))
            {
                File.Delete(Cfg.LocalFilePath);
            }

            await using (var sourceFileStream = new FileStream(
                             this._tempFilePath, FileMode.Open, FileAccess.Read,
                             FileShare.Read, BufferSize, true))
            {
                await using (var destFileStream = new FileStream(
                                 Cfg.LocalFilePath, FileMode.OpenOrCreate, FileAccess.Write,
                                 FileShare.Write, BufferSize, true))
                {
                    await sourceFileStream.CopyToAsync(destFileStream, cancelToken);
                }
            }

            File.Delete(this._tempFilePath);
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine("Operation was cancelled during file copy.");
            return Array.Empty<byte>();
        }
        catch (IOException ex)
        {
            return await _OnDownloadError(ex.ToString());
        }
        catch (Exception ex)
        {
            return await _OnDownloadError(ex.ToString());
        }

        Cfg.DownloadCallback?.Invoke(EDownloadStatus.Succeed, new DownloadInfo()
        {
            StrResult = Cfg.LocalFilePath,
        });
        return System.Text.Encoding.UTF8.GetBytes(Cfg.LocalFilePath);
    }
}

public class SimpleDownloader : Downloader
{
    public SimpleDownloader(DownloadConfig cfg) : base(cfg)
    {
    }

    public override async Task<byte[]> Download(CancellationToken cancelToken)
    {
        this._cancelToken = cancelToken;
        cancelToken.ThrowIfCancellationRequested();
        if (this._httpClient == null)
        {
            this._httpClient = new HttpClient();
        }
        using (this._httpClient)
        {
            if (Cfg.RequestTimeout > 0)
            {
                this._httpClient.Timeout = TimeSpan.FromSeconds(Cfg.RequestTimeout);
            }

            try
            {
                var response = await this._httpClient.GetAsync(Cfg.RemoteUrl, cancelToken);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(cancelToken);
                Cfg.DownloadCallback?.Invoke(EDownloadStatus.Succeed, new DownloadInfo()
                {
                    ByteResult = bytes,
                });
                return bytes;
            }
            catch (OperationCanceledException ex)
            {
                Console.WriteLine("Operation was cancelled.");
                return Array.Empty<byte>();
            }
            catch (HttpRequestException ex)
            {
                return await _OnDownloadError(ex.ToString());
            }
            catch (Exception ex)
            {
                return await _OnDownloadError(ex.ToString());
            }
        }
    }
}

public class DownloadHelper
{
    private Dictionary<int, Downloader> _downloaders = new Dictionary<int, Downloader>();

    public async Task<string> CreateDownloader(DownloadConfig cfg, bool start = true)
    {
        var downloader = new DCFSDownloader(cfg);
        _downloaders.Add(downloader.Id, downloader);
        if (start)
        {
            var bytes = await downloader.Download(CancellationToken.None);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        return string.Empty;
    }
}