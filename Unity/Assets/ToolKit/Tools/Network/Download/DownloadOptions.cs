/*
 * datetime     : 2026/10/30
 * description  : 下载策略参数
 */

using System;

namespace ToolKit.Tools.Network
{
    /// <summary>
    /// 单个下载任务的策略参数
    /// <para>重试、超时、缓冲区和限速集中在此处, 默认值只定义一份</para>
    /// <para>属性赋值时会收敛到有效范围, 越界值不会写入</para>
    /// </summary>
    public sealed class DownloadOptions
    {
        /// <summary> 默认最大重试次数 </summary>
        public const int DefaultMaxRetries = 3;

        /// <summary> 默认首次重试等待时间 (毫秒) </summary>
        public const int DefaultRetryDelayMs = 1000;

        /// <summary> 默认重试等待时间上限 (毫秒) </summary>
        public const int DefaultMaxRetryDelayMs = 30000;

        /// <summary> 默认读写缓冲区大小 (字节) </summary>
        public const int DefaultBufferSize = 8192;

        /// <summary> 默认连接超时 (毫秒) </summary>
        public const int DefaultConnectTimeoutMs = 30000;

        /// <summary> 默认读取超时 (毫秒) </summary>
        public const int DefaultReadTimeoutMs = 30000;

        /// <summary> 缓冲区下限 (字节) </summary>
        public const int MinBufferSize = 1024;

        /// <summary> 超时下限 (毫秒) </summary>
        public const int MinTimeoutMs = 1000;

        private int _maxRetries = DefaultMaxRetries;
        private int _retryDelayMs = DefaultRetryDelayMs;
        private int _maxRetryDelayMs = DefaultMaxRetryDelayMs;
        private int _bufferSize = DefaultBufferSize;
        private int _connectTimeoutMs = DefaultConnectTimeoutMs;
        private int _readTimeoutMs = DefaultReadTimeoutMs;
        private long _maxBytesPerSecond;

        /// <summary> 最大重试次数 (0 表示不重试) </summary>
        public int MaxRetries
        {
            get => _maxRetries;
            set => _maxRetries = Math.Max(0, value);
        }

        /// <summary> 首次重试的等待时间 (毫秒), 之后每次翻倍 </summary>
        public int RetryDelayMs
        {
            get => _retryDelayMs;
            set => _retryDelayMs = Math.Max(0, value);
        }

        /// <summary> 重试等待时间上限 (毫秒), 指数增长到此值后不再增加 </summary>
        public int MaxRetryDelayMs
        {
            get => _maxRetryDelayMs;
            set => _maxRetryDelayMs = Math.Max(0, value);
        }

        /// <summary> 读写缓冲区大小 (字节), 最小 1024 </summary>
        public int BufferSize
        {
            get => _bufferSize;
            set => _bufferSize = Math.Max(MinBufferSize, value);
        }

        /// <summary> 连接超时 (毫秒): 建立连接并收到响应头的最大等待时间, 最小 1000 </summary>
        public int ConnectTimeoutMs
        {
            get => _connectTimeoutMs;
            set => _connectTimeoutMs = Math.Max(MinTimeoutMs, value);
        }

        /// <summary> 读取超时 (毫秒): 单次读取的最大等待时间, 最小 1000 </summary>
        public int ReadTimeoutMs
        {
            get => _readTimeoutMs;
            set => _readTimeoutMs = Math.Max(MinTimeoutMs, value);
        }

        /// <summary> 最大下载速度 (字节/秒), 小于等于 0 表示不限速 </summary>
        public long MaxBytesPerSecond
        {
            get => _maxBytesPerSecond;
            set => _maxBytesPerSecond = Math.Max(0, value);
        }

        /// <summary>
        /// 复制一份设置
        /// <para>任务创建时固化参数, 调用方之后修改原对象不影响已创建的任务</para>
        /// </summary>
        public DownloadOptions Copy()
        {
            return new DownloadOptions
            {
                MaxRetries = _maxRetries,
                RetryDelayMs = _retryDelayMs,
                MaxRetryDelayMs = _maxRetryDelayMs,
                BufferSize = _bufferSize,
                ConnectTimeoutMs = _connectTimeoutMs,
                ReadTimeoutMs = _readTimeoutMs,
                MaxBytesPerSecond = _maxBytesPerSecond,
            };
        }
    }
}
