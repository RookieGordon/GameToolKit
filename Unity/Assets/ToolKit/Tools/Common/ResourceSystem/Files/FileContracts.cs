/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 下载文件缓存契约 (删减版)。保留稳定身份、来源、有效性与网络配置；
 *                本地有有效文件就复用，没有就下载；启动前清理一次，运行期不清理已完成文件。
 *                旧容量账、租约、元数据事务与快照模型已按删减说明移除。
 */

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ToolKit.Tools.Common
{
    /// <summary> 文件身份：Namespace + ArtifactId + Revision + Variant。不同上下文可访问不同内容时必须产生不同身份 </summary>
    public readonly struct FileIdentity : IEquatable<FileIdentity>
    {
        public readonly string Namespace;
        public readonly string ArtifactId;
        public readonly string Revision;
        public readonly string Variant;

        public FileIdentity(string ns, string artifactId, string revision, string variant)
        {
            Namespace = ns ?? "";
            ArtifactId = artifactId ?? "";
            Revision = revision ?? "";
            Variant = variant ?? "";
        }

        public bool Equals(FileIdentity other)
        {
            return string.Equals(Namespace, other.Namespace, StringComparison.Ordinal)
                   && string.Equals(ArtifactId, other.ArtifactId, StringComparison.Ordinal)
                   && string.Equals(Revision, other.Revision, StringComparison.Ordinal)
                   && string.Equals(Variant, other.Variant, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is FileIdentity other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Namespace.GetHashCode();
                hash = (hash * 397) ^ ArtifactId.GetHashCode();
                hash = (hash * 397) ^ Revision.GetHashCode();
                hash = (hash * 397) ^ Variant.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
        {
            return $"{Namespace}/{ArtifactId}@{Revision}~{Variant}";
        }
    }

    public enum ValidityMode
    {
        /// <summary> 调用方承诺身份对应内容不变 </summary>
        Immutable,

        /// <summary> 到期停止复用内存记录，重新下载到新路径；旧路径留下次启动处理 </summary>
        ExpiresAfter,
    }

    /// <summary> 有效性规则：同一身份的规则必须一致 </summary>
    public readonly struct FileValidity : IEquatable<FileValidity>
    {
        public readonly ValidityMode Mode;
        public readonly TimeSpan Ttl;

        private FileValidity(ValidityMode mode, TimeSpan ttl)
        {
            Mode = mode;
            Ttl = ttl;
        }

        public static FileValidity Immutable { get; } = new FileValidity(ValidityMode.Immutable, TimeSpan.Zero);

        public static FileValidity ExpiresAfter(TimeSpan ttl)
        {
            if (ttl <= TimeSpan.Zero)
            {
                throw new ArgumentException("ExpiresAfter 的 ttl 必须为正", nameof(ttl));
            }
            return new FileValidity(ValidityMode.ExpiresAfter, ttl);
        }

        public bool IsCompatibleWith(FileValidity other)
        {
            if (Mode != other.Mode)
            {
                return false;
            }
            return Mode == ValidityMode.Immutable || Ttl.Equals(other.Ttl);
        }

        public bool Equals(FileValidity other)
        {
            return Mode == other.Mode && Ttl.Equals(other.Ttl);
        }

        public override bool Equals(object? obj)
        {
            return obj is FileValidity other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Mode * 397) ^ Ttl.GetHashCode();
            }
        }
    }

    /// <summary> 传输上下文：请求头与重定向策略；原文不进入日志 </summary>
    public sealed class TransportContext
    {
        public static readonly TransportContext Default = new TransportContext();

        public IReadOnlyDictionary<string, string> Headers { get; }
        public bool AllowRedirects { get; }
        public int MaxRedirects { get; }

        public TransportContext(IReadOnlyDictionary<string, string>? headers = null,
            bool allowRedirects = true, int maxRedirects = 5)
        {
            Headers = headers ?? new Dictionary<string, string>();
            AllowRedirects = allowRedirects;
            MaxRedirects = maxRedirects;
        }
    }

    /// <summary> 一次文件获取请求；ExpectedLength/ExpectedSha256 参与身份冲突校验与交付核验 </summary>
    public sealed class FileRequest
    {
        public readonly FileIdentity Identity;
        public readonly Uri Source;
        public readonly TransportContext TransportContext;
        public readonly FileValidity Validity;
        public readonly long? ExpectedLength;
        public readonly string? ExpectedSha256;

        public FileRequest(FileIdentity identity, Uri source, TransportContext? transportContext,
            FileValidity validity, long? expectedLength = null, string? expectedSha256 = null)
        {
            Identity = identity;
            Source = source ?? throw new ArgumentNullException(nameof(source));
            TransportContext = transportContext ?? TransportContext.Default;
            Validity = validity;
            ExpectedLength = expectedLength;
            ExpectedSha256 = expectedSha256;
        }
    }

    /// <summary>
    /// 缓存配置：Directory 为专用缓存根目录；StartupTargetBytes 只决定启动时尝试保留多少文件
    /// (null 不按总量删除；0 尽力清空；负数非法)，运行期无容量硬上限。
    /// </summary>
    public sealed class FileCacheOptions
    {
        public string Directory { get; set; } = "";
        public long? StartupTargetBytes { get; set; } = 512L * 1024 * 1024;

        public FileCacheOptions Clone()
        {
            return new FileCacheOptions
            {
                Directory = Directory,
                StartupTargetBytes = StartupTargetBytes,
            };
        }

        public void Validate()
        {
            if (string.IsNullOrEmpty(Directory))
            {
                throw new ArgumentException("FileCacheOptions.Directory 不能为空");
            }
            if (StartupTargetBytes is long target && target < 0)
            {
                throw new ArgumentException("StartupTargetBytes 不能为负");
            }
        }
    }

    /// <summary> 下载行为配置：并发、超时与有限重试 </summary>
    public sealed class NetworkOptions
    {
        public int MaxConcurrentDownloads { get; set; } = 4;
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public int MaxRetries { get; set; } = 2;
        public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(0.5);

        public NetworkOptions Clone()
        {
            return new NetworkOptions
            {
                MaxConcurrentDownloads = MaxConcurrentDownloads,
                ConnectTimeout = ConnectTimeout,
                ResponseTimeout = ResponseTimeout,
                MaxRetries = MaxRetries,
                RetryBaseDelay = RetryBaseDelay,
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
        }
    }

    /// <summary> 身份 → 持久化键：长度前缀编码 + SHA-256，无歧义且路径安全 </summary>
    public static class FileKeyEncoding
    {
        public static string Encode(FileIdentity identity)
        {
            var raw = new StringBuilder();
            AppendPart(raw, identity.Namespace);
            AppendPart(raw, identity.ArtifactId);
            AppendPart(raw, identity.Revision);
            AppendPart(raw, identity.Variant);
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw.ToString()));
                var hex = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    hex.Append(b.ToString("x2"));
                }
                return hex.ToString();
            }
        }

        private static void AppendPart(StringBuilder builder, string part)
        {
            // 长度前缀编码消除分隔符歧义
            builder.Append((part ?? "").Length).Append(':').Append(part ?? "").Append('|');
        }
    }
}
