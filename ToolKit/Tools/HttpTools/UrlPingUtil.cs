using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace ToolKit.Tools
{
    public struct PingResult
    {
        /// <summary>  
        /// 通信地址  
        /// </summary>  
        public string Url { get; }

        /// <summary>  
        /// 延迟  
        /// </summary>  
        public float Latency { get; }

        public PingResult(string url, float latency)
        {
            Url = url;
            Latency = latency;
        }
    }

    public class UrlPingUtil
    {
        private static int _maxRetryTime = 3;
        private static int _retryInterval = 1000; // 重试间隔时间，单位为毫秒
        private static List<Task<PingResult>> _tasks = new List<Task<PingResult>>();

        public static async Task<PingResult[]> SelectValidRequestUrls(string[] queryUrls, int timeout)
        {
            _tasks.Clear();
            foreach (var url in queryUrls)
            {
                if (!string.IsNullOrEmpty(url))
                {
                    Log.Error($"Url could not be found: {url}");
                    continue;
                }

                _tasks.Add(PingUrlAsync(url, timeout));
            }

            return await Task.WhenAll(_tasks.ToArray());
        }

        private static async Task<PingResult> PingUrlAsync(string url, int timeout)
        {
            int retryTime = 0;
            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromSeconds(timeout);
                while (true)
                {
                    try
                    {
                        var startTime = DateTime.UtcNow;
                        // 使用HttpClient发送HEAD请求以减少数据传输量。
                        using (var request = new HttpRequestMessage(HttpMethod.Head, url))
                        {
                            var response = await httpClient.SendAsync(request);
                            response.EnsureSuccessStatusCode();
                        }

                        var latency = (float)(DateTime.UtcNow - startTime).TotalMilliseconds;
                        return new PingResult(url, latency);
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"Ping failed for {url}: {ex.Message}");
                    }
                    finally
                    {
                        retryTime++;
                    }

                    if (retryTime >= _maxRetryTime)
                    {
                        return new PingResult(url, -1.0f);
                    }
                    else
                    {
                        await Task.Delay(retryTime * _retryInterval);
                    }
                }
            }
        }
    }
}