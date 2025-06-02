using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Common;
using ToolKit.Tools;

namespace ToolKit.Tools
{
    public class DownloadUtil
    {
        public static async Task<DownloadResult> Download(string url, string saveDir, DownloadConfig config,
            CancellationToken token = default)
        {
            return await new HttpDownloader(url, saveDir, config, token).Download();
        }
    }

    public struct DownloadResult
    {
        public ETaskResult Result;
        public string ErrorMsg;
        public string FilePath;
    }

    public struct DownloadUpdateInfo
    {
        public long CurrentSize;
        public long TotalSize;
        public float Progress;
    }

    public struct DownloadConfig
    {
        public int Timeout;
        public int RetryCount;
        public Action StartDownload;
        public Action<DownloadUpdateInfo> UpdateDownload;
    }

    public class HttpDownloader
    {
        public const int RETRY_INTERVAL = 5;
        public const int BUFF_SIZE = 81920;
        public string Url { get; }
        public string SaveDir { get; }
        public DownloadConfig Config { get; }
        public CancellationToken CancellationToken { get; }

        private int _retryCount = 0;
        private string _tempFilePath;
        private long _fileTotalSize;

        private HttpClient _httpClient;

        public HttpDownloader(string url, string saveDir, DownloadConfig config,
            CancellationToken cancellationToken = default)
        {
            Url = url;
            SaveDir = saveDir;
            Config = config;
            CancellationToken = cancellationToken;

            var fileName = Path.GetFileName(url);
            _tempFilePath = Path.Combine(saveDir, fileName + ".tmp");
        }

        public async Task<DownloadResult> Download()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(Config.Timeout);

            Config.StartDownload?.Invoke();
            DownloadResult result;
            while (true)
            {
                result = await InnerDownload();
                if (result.Result != ETaskResult.Failed)
                {
                    break;
                }

                _retryCount++;
                if (_retryCount >= Config.RetryCount)
                {
                    break;
                }

                try
                {
                    await Task.Delay(RETRY_INTERVAL, CancellationToken);
                }
                catch (TaskCanceledException e)
                {
                    result = new DownloadResult()
                    {
                        Result = ETaskResult.Cancelled,
                        ErrorMsg = e.Message,
                    };
                    break;
                }
            }

            _httpClient.Dispose();
            return result;
        }

        private async Task<DownloadResult> InnerDownload()
        {
            try
            {
                using (var request = await PreVerify())
                {
                    using (var response =
                           await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                               CancellationToken))
                    {
                        // 获取需要下载的文件的总大小
                        _fileTotalSize = response.Content.Headers.ContentLength ?? 0;
                        if (response.StatusCode == HttpStatusCode.OK ||
                            response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            // HttpStatusCode.PartialContent表示不支持断点续传, 使用FileMode.Create重写文件
                            var fileMode = response.StatusCode == HttpStatusCode.OK ? FileMode.Create : FileMode.Append;
                            using (var fileStream = new FileStream(_tempFilePath, fileMode, FileAccess.Write))
                            {
                                using (var responseStream = await response.Content.ReadAsStreamAsync())
                                {
                                    await WriteFile(fileStream, responseStream, CancellationToken);
                                }
                            }

                            var newFilePath = _tempFilePath.Replace(".tmp", "");
                            File.Move(_tempFilePath, newFilePath);
                            return new DownloadResult()
                            {
                                Result = ETaskResult.Succeed,
                                FilePath = newFilePath,
                            };
                        }
                        else
                        {
                            return new DownloadResult()
                            {
                                Result = ETaskResult.Failed,
                                ErrorMsg = $"Unhandled status code: {response.StatusCode}",
                            };
                        }
                    }
                }
            }
            catch (TaskCanceledException e)
            {
                return new DownloadResult()
                {
                    Result = ETaskResult.Cancelled,
                    ErrorMsg = e.Message,
                };
            }
            catch (Exception e)
            {
                return new DownloadResult()
                {
                    Result = ETaskResult.Failed,
                    ErrorMsg = e.Message
                };
            }
        }

        private async Task<HttpRequestMessage> PreVerify()
        {
            // 设置验证头
            using (var headRequest = new HttpRequestMessage(HttpMethod.Head, Url))
            {
                using (var headResponse = await _httpClient.SendAsync(headRequest, CancellationToken))
                {
                    if (!headResponse.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(headResponse.ReasonPhrase);
                    }

                    // 获取文件的ETag和Last-Modified
                    var initialEtag = headResponse.Headers.ETag?.Tag ?? "";
                    var initialLastModified = headResponse.Content.Headers.LastModified ?? DateTimeOffset.MinValue;

                    long existLength = 0;
                    if (File.Exists(_tempFilePath))
                    {
                        var fileInfo = new FileInfo(_tempFilePath);
                        existLength = fileInfo.Length;
                    }

                    // 构造续传请求
                    var request = new HttpRequestMessage(HttpMethod.Get, Url);
                    request.Headers.Range = new RangeHeaderValue(existLength, null);

                    // 添加条件校验头
                    if (!string.IsNullOrEmpty(initialEtag))
                    {
                        request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(initialEtag));
                    }
                    else if (initialLastModified != DateTimeOffset.MinValue)
                    {
                        request.Headers.IfRange = new RangeConditionHeaderValue(initialLastModified);
                    }

                    return request;
                }
            }
        }

        private async Task WriteFile(FileStream fileStream, Stream responseStream,
            CancellationToken cancellationToken = default)
        {
            byte[] buffer = new byte[BUFF_SIZE];
            int bytes;
            long bytesRead = 0;
            responseStream.ReadTimeout = Config.Timeout;
            while (true)
            {
                bytes = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                if (bytes == 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer, 0, bytes, cancellationToken);
                bytesRead += bytes;
                Config.UpdateDownload?.Invoke(new DownloadUpdateInfo()
                {
                    CurrentSize = bytesRead,
                    TotalSize = _fileTotalSize,
                    Progress = (float)bytesRead / _fileTotalSize
                });
            }
        }
    }
}