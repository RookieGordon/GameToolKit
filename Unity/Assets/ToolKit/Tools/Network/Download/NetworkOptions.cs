/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 下载行为配置 (并发、超时与重试)。属于下载模块，定义位置不在资源缓存契约文件中。
 */

using System;

namespace ToolKit.Tools.Network
{
    public sealed class NetworkOptions
    {
        public int MaxConcurrentDownloads { get; set; } = 4;
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public int MaxRetries { get; set; } = 2;
        public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(0.5);
        public int BufferSize { get; set; } = 81920;

        public NetworkOptions Clone()
        {
            return new NetworkOptions
            {
                MaxConcurrentDownloads = MaxConcurrentDownloads,
                ConnectTimeout = ConnectTimeout,
                ResponseTimeout = ResponseTimeout,
                MaxRetries = MaxRetries,
                RetryBaseDelay = RetryBaseDelay,
                BufferSize = BufferSize,
            };
        }

        public void Validate()
        {
            if (MaxConcurrentDownloads <= 0)
            {
                throw new ArgumentException("MaxConcurrentDownloads 必须为正");
            }
            if (ConnectTimeout <= TimeSpan.Zero || ResponseTimeout <= TimeSpan.Zero || RetryBaseDelay < TimeSpan.Zero)
            {
                throw new ArgumentException("网络超时与重试延迟不能为负");
            }
            if (MaxRetries < 0)
            {
                throw new ArgumentException("MaxRetries 不能为负");
            }
            if (BufferSize < 1024)
            {
                throw new ArgumentException("BufferSize 至少 1024");
            }
        }
    }
}
