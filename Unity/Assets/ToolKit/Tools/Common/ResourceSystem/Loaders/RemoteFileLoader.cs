/*
 * author       : Gordon
 * datetime     : 2026/6/27
 * description  : 远程文件加载器 (引擎无关)。流程: 远程 URL -> 下载到本地缓存 -> 读为 byte[]。
 *                - "下载一次, 之后走本地": 命中本地缓存文件时跳过下载, 直接读取;
 *                - 缓存路径策略可注入 (url -> 本地 path): 默认 MD5 落在 cacheRoot 下;
 *                  自定义映射可掌控缓存布局, 也可"预置缓存"(把文件提前按规律放好, 首次即命中本地);
 *                - 下载进度通过 OnDownloadProgress 事件上报 (通用加载 API 不再传 IProgress);
 *                - 复用 Network/Download 模块 (SimpleDownloader/DownloadTask), 支持断点续传/重试。
 */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Network;

namespace ToolKit.Tools.Common
{
    public sealed class RemoteFileLoader : ILoader
    {
        private readonly Func<string, string> _urlToPath;   // url -> 本地缓存路径 (映射规律)
        private readonly int _maxRetries;

        public ELoadType LoadType => ELoadType.RemoteFile;

        /// <summary>
        /// 下载进度事件 (url, 进度)。仅远端下载时触发; 命中本地缓存不触发。
        /// </summary>
        public event Action<string, DownloadProgress> OnDownloadProgress;

        /// <summary>
        /// 默认缓存策略: URL 的 MD5 + 原扩展名, 落在 cacheRoot 下。
        /// </summary>
        /// <param name="cacheRoot">本地缓存根目录 (Unity 侧通常传 Application.persistentDataPath)</param>
        /// <param name="maxRetries">下载失败重试次数</param>
        public RemoteFileLoader(string cacheRoot, int maxRetries = 3)
            : this(_MakeDefaultMapper(cacheRoot), maxRetries)
        {
        }

        /// <summary>
        /// 自定义缓存策略: 由外部决定每个 url 映射到哪个本地 path。
        /// </summary>
        /// <param name="urlToLocalPath">url -> 本地缓存路径 的映射规律</param>
        /// <param name="maxRetries">下载失败重试次数</param>
        public RemoteFileLoader(Func<string, string> urlToLocalPath, int maxRetries = 3)
        {
            _urlToPath = urlToLocalPath ?? throw new ArgumentNullException(nameof(urlToLocalPath));
            _maxRetries = maxRetries;
        }

        public bool CanLoad(string address)
        {
            return !string.IsNullOrEmpty(address) &&
                   (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    address.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<IAssetHandle> LoadAsync(
            string address,
            CancellationToken cancellationToken = default)
        {
            var handle = new AssetHandle(address);
            try
            {
                var cachePath = _urlToPath(address);

                // 1. 未命中本地缓存 -> 下载 (命中则直接读本地, 不下载)
                if (!File.Exists(cachePath))
                {
                    try
                    {
                        await _DownloadAsync(address, cachePath, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        handle.SetFailed(ELoadError.NetworkError, $"远端下载失败: {address}", e);
                        return handle;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 2. 读取本地缓存文件
                try
                {
                    var bytes = await _ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
                    handle.SetSucceed(bytes, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    handle.SetFailed(ELoadError.IOError, $"读取缓存文件失败: {cachePath}", e);
                }
            }
            catch (OperationCanceledException)
            {
                handle.SetCancelled();
            }

            return handle;
        }

        /// <summary> 默认映射: URL 的 MD5 + 原扩展名, 落在 cacheRoot 下 </summary>
        private static Func<string, string> _MakeDefaultMapper(string cacheRoot)
        {
            if (string.IsNullOrEmpty(cacheRoot))
            {
                throw new ArgumentException("cacheRoot 不能为空", nameof(cacheRoot));
            }
            return url =>
            {
                string ext = Path.GetExtension(url);
                string hash;
                using (var md5 = MD5.Create())
                {
                    var data = md5.ComputeHash(Encoding.UTF8.GetBytes(url));
                    var sb = new StringBuilder(data.Length * 2);
                    foreach (var b in data)
                    {
                        sb.Append(b.ToString("x2"));
                    }
                    hash = sb.ToString();
                }
                return Path.Combine(cacheRoot, hash + ext);
            };
        }

        private Task _DownloadAsync(string url, string savePath, CancellationToken cancellationToken)
        {
            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var downloader = new SimpleDownloader(maxConcurrency: 1);
            var task = downloader.CreateTask(url, savePath, maxRetries: _maxRetries);

            task.OnProgress = (_, p) => OnDownloadProgress?.Invoke(url, p);
            task.OnCompleted = _ => tcs.TrySetResult(true);
            task.OnFailed = (_, err) => tcs.TrySetException(new IOException($"远程文件下载失败: {url}, error={err}"));
            task.OnCancelled = _ => tcs.TrySetCanceled();

            // 外部取消令牌 -> 取消下载
            var reg = cancellationToken.Register(() =>
            {
                task.Cancel();
                tcs.TrySetCanceled();
            });

            downloader.AddTask(task);
            downloader.Start();

            return tcs.Task.ContinueWith(t =>
            {
                reg.Dispose();
                downloader.Dispose();
                if (t.IsFaulted && t.Exception != null)
                {
                    throw t.Exception.GetBaseException();
                }
                if (t.IsCanceled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }, TaskScheduler.Default);
        }

        private static async Task<byte[]> _ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            const int chunk = 81920;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, chunk, useAsync: true))
            {
                var result = new byte[fs.Length];
                if (result.Length == 0)
                {
                    return result;
                }

                // 传输缓冲走 BytesPool, 降低大文件读取的堆分配
                var buffer = BytesPool.Rent(chunk);
                try
                {
                    int written = 0;
                    int read;
                    while ((read = await fs.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        Buffer.BlockCopy(buffer, 0, result, written, read);
                        written += read;
                    }
                }
                finally
                {
                    BytesPool.Return(buffer);
                }
                return result;
            }
        }
    }
}