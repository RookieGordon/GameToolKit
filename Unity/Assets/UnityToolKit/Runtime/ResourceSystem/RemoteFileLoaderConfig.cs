using System;
using System.IO;
using ToolKit.Tools.Common;
using ToolKit.Tools.Network;
using UnityEngine;

namespace UnityToolKit.Runtime.ResourceSystem
{
    /// <summary>远程文件加载器的缓存及下载参数；配置资产只保存参数，在 Install 装配时固化独立运行时依赖。</summary>
    [Serializable]
    public sealed class RemoteFileLoaderConfig : LoaderConfig
    {
        [Tooltip("相对路径以 Application.persistentDataPath 为基准，绝对路径直接使用。目录由缓存独占；初始化会删除临时文件并清理旧文件。")]
        public string CacheDirectory = "ResourceCache";

        [Tooltip("初始化时将磁盘缓存清理到此大小以内，单位为字节，必须大于 0；默认 512 MiB。这不是运行期间的容量硬上限。")]
        public long StartupTargetBytes = 512L * 1024 * 1024;

        [Min(0), Tooltip("磁盘缓存文件的有效期，单位为秒；0 表示不因年龄过期。不会刷新资源系统中已经加载的内存产物。")]
        public double MaxAgeSeconds;

        [Min(1), Tooltip("内置 SimpleDownloader 同时下载的文件数，必须大于 0。")]
        public int MaxConcurrentDownloads = 4;

        [Header("下载策略（内置 SimpleDownloader）")]
        [Min(0), Tooltip("单次下载失败后的最大重试次数；0 表示不重试。")]
        public int MaxRetries = DownloadOptions.DefaultMaxRetries;

        [Min(0), Tooltip("首次重试等待的毫秒数，之后逐次翻倍，但不超过重试等待上限。")]
        public int RetryDelayMs = DownloadOptions.DefaultRetryDelayMs;

        [Min(0), Tooltip("每次重试最多等待的毫秒数；0 表示重试不等待。")]
        public int MaxRetryDelayMs = DownloadOptions.DefaultMaxRetryDelayMs;

        [Min(DownloadOptions.MinBufferSize), Tooltip("每个下载任务的读写缓冲区，单位字节；最小 1024。")]
        public int BufferSize = DownloadOptions.DefaultBufferSize;

        [Min(DownloadOptions.MinTimeoutMs), Tooltip("每次尝试建立连接并等待响应头的超时，单位毫秒；最小 1000。")]
        public int ConnectTimeoutMs = DownloadOptions.DefaultConnectTimeoutMs;

        [Min(DownloadOptions.MinTimeoutMs), Tooltip("每次读取响应内容的超时，单位毫秒；最小 1000，不包括本地写入和限速等待。")]
        public int ReadTimeoutMs = DownloadOptions.DefaultReadTimeoutMs;

        [Tooltip("每个下载任务的速度上限，单位字节/秒；0 表示不限速。这不是所有任务的合计上限。")]
        public long MaxBytesPerSecond;

        public RemoteFileLoaderConfig() { Source = ResourceSource.Remote; }

        public override void Validate()
        {
            CreatePolicy();
            if (StartupTargetBytes <= 0)
                throw new InvalidOperationException("StartupTargetBytes 必须大于 0（单位：字节）。");
            if (MaxConcurrentDownloads <= 0)
                throw new InvalidOperationException("MaxConcurrentDownloads 必须大于 0。");
            CreateMaxAge();
            ResolveCacheDirectory();
            CreateDownloadOptions();
        }

        /// <summary>创建独立下载策略快照。</summary>
        public DownloadOptions CreateDownloadOptions()
        {
            RequireMinimum(MaxRetries, 0, nameof(MaxRetries));
            RequireMinimum(RetryDelayMs, 0, nameof(RetryDelayMs));
            RequireMinimum(MaxRetryDelayMs, 0, nameof(MaxRetryDelayMs));
            RequireMinimum(BufferSize, DownloadOptions.MinBufferSize, nameof(BufferSize));
            RequireMinimum(ConnectTimeoutMs, DownloadOptions.MinTimeoutMs, nameof(ConnectTimeoutMs));
            RequireMinimum(ReadTimeoutMs, DownloadOptions.MinTimeoutMs, nameof(ReadTimeoutMs));
            RequireMinimum(MaxBytesPerSecond, 0, nameof(MaxBytesPerSecond));
            return new DownloadOptions
            {
                MaxRetries = MaxRetries,
                RetryDelayMs = RetryDelayMs,
                MaxRetryDelayMs = MaxRetryDelayMs,
                BufferSize = BufferSize,
                ConnectTimeoutMs = ConnectTimeoutMs,
                ReadTimeoutMs = ReadTimeoutMs,
                MaxBytesPerSecond = MaxBytesPerSecond
            };
        }

        public override void Install(ResourceSystemOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            Validate();

            var downloader = new SimpleDownloader(MaxConcurrentDownloads, CreateDownloadOptions());
            var cache = new FileCache(
                new HashedPathConverter(ResolveCacheDirectory()),
                RemoteFileLoader.UseDownloader(downloader),
                StartupTargetBytes,
                CreateMaxAge());
            var policy = CreatePolicy();

            options.Initializers.Add(async token =>
            {
                token.ThrowIfCancellationRequested();
                await cache.InitializeAsync(token).ConfigureAwait(false);
                downloader.Start();
            });
            options.ShutdownActions.Add(async () =>
            {
                try { await cache.StopAsync().ConfigureAwait(false); }
                finally { downloader.Dispose(); }
            });
            options.AddLoader(Source, new RemoteFileLoader(cache), policy);
        }

        public override ILoader CreateLoader()
            => throw new NotSupportedException("远程加载器包含缓存清理与下载器关闭责任，请通过 Install / ConvertToOptions 装配，或直接使用 FileCache 与 RemoteFileLoader。");

        private static void RequireMinimum(long value, long minimum, string field)
        {
            if (value < minimum)
                throw new InvalidOperationException($"{field} 必须大于或等于 {minimum}。");
        }

        private string ResolveCacheDirectory()
        {
            if (string.IsNullOrWhiteSpace(CacheDirectory))
                throw new InvalidOperationException("CacheDirectory 不能为空。");
            try
            {
                var path = Path.IsPathRooted(CacheDirectory)
                    ? CacheDirectory : Path.Combine(Application.persistentDataPath, CacheDirectory);
                return Path.GetFullPath(path);
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
            {
                throw new InvalidOperationException("CacheDirectory 不是有效的缓存目录：" + error.Message, error);
            }
        }

        private TimeSpan? CreateMaxAge()
        {
            if (double.IsNaN(MaxAgeSeconds) || double.IsInfinity(MaxAgeSeconds) || MaxAgeSeconds < 0)
                throw new InvalidOperationException("MaxAgeSeconds 必须是有限的非负数；0 表示不过期。");
            if (MaxAgeSeconds == 0) return null;
            TimeSpan maxAge;
            try { maxAge = TimeSpan.FromSeconds(MaxAgeSeconds); }
            catch (OverflowException error)
            {
                throw new InvalidOperationException("MaxAgeSeconds 超出了 TimeSpan 可表示的范围。", error);
            }
            if (maxAge <= TimeSpan.Zero)
                throw new InvalidOperationException("MaxAgeSeconds 太小，转换后的有效期不能为 0。");
            return maxAge;
        }
    }
}
