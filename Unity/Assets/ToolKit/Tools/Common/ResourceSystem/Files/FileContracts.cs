/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 磁盘缓存契约 (P4, §10.2)。FileIdentity 四元组经长度前缀编码后 SHA-256 作为持久化键，
 *                禁止 GetHashCode 或歧义拼接；FileValidity 仅两种模式 (Immutable / ExpiresAfter)；
 *                同一内容可有不同临时签名 URL，故 Source 与缓存身份分开。
 */

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ToolKit.Tools.Common.Resource
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

        /// <summary> 到期视为未命中；第一版到期重新下载，不实现条件请求与断点续传 </summary>
        ExpiresAfter,
    }

    /// <summary> 有效性规则：同一身份的规则必须一致 (§10.6 IdentityConflict 校验) </summary>
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

    /// <summary>
    /// 传输上下文：请求头、重定向策略。原文不进入磁盘索引与日志；
    /// 默认允许最多 5 次重定向，拒绝跨主机转发敏感鉴权头 (HttpClient 行为)。
    /// </summary>
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

    /// <summary> 一次文件获取请求；ExpectedLength/ExpectedSha256 参与身份冲突校验 </summary>
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

    public enum FileCacheState
    {
        Initializing,
        Open,
        Closing,
        Closed,
        Faulted,
    }

    public enum FileEntryState
    {
        Ready,
        Retired,
        Deleting,
        Garbage,
    }

    public enum DownloadJobState
    {
        Queued,
        Opening,
        Downloading,
        Verifying,
        Committing,
        Succeeded,
        Failed,
        Abandoning,
        Cleaned,
    }

    /// <summary> 缓存配置 (§9.1)：容量包含数据、临时文件与缓存管理文件；TrimToRatio 为尽力回收目标 </summary>
    public sealed class FileCacheOptions
    {
        public string Directory { get; set; } = "";
        public long MaxBytes { get; set; } = 512 * 1024 * 1024;
        public double TrimToRatio { get; set; } = 0.8;
        public int ChunkBytes { get; set; } = 256 * 1024;
        public int MetadataAllowanceBytes { get; set; } = 16 * 1024;

        /// <summary>
        /// 自定义相对路径映射 (identity, generation) → 相对根目录的目录段；
        /// 返回值仅允许相对目录，实际文件名含框架生成的 key 与 generation 后缀。
        /// </summary>
        public Func<FileIdentity, long, string>? ResolvePath { get; set; }

        public void Validate()
        {
            if (string.IsNullOrEmpty(Directory))
            {
                throw new ArgumentException("FileCacheOptions.Directory 不能为空");
            }
            if (MaxBytes <= 0)
            {
                throw new ArgumentException("FileCacheOptions.MaxBytes 必须为正");
            }
            if (TrimToRatio <= 0 || TrimToRatio > 1)
            {
                throw new ArgumentException("FileCacheOptions.TrimToRatio 必须在 (0,1] 内");
            }
            if (ChunkBytes <= 0)
            {
                throw new ArgumentException("FileCacheOptions.ChunkBytes 必须为正");
            }
            if (MetadataAllowanceBytes <= 0)
            {
                throw new ArgumentException("FileCacheOptions.MetadataAllowanceBytes 必须为正");
            }
        }
    }

    public sealed class NetworkOptions
    {
        public int MaxConcurrentDownloads { get; set; } = 4;
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public int MaxRetries { get; set; } = 2;
        public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(0.5);

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

    public sealed class FileCacheSnapshot
    {
        public FileCacheState State;
        public long AccountedBytes;
        public long ReservedBytes;
        public long MaxBytes;
        public int Pins;
        public int Jobs;
        public List<FileRow> FileRows = new List<FileRow>();
    }

    public readonly struct FileRow
    {
        public readonly string Key;
        public readonly long Generation;
        public readonly FileEntryState State;
        public readonly long Bytes;
        public readonly int Pins;

        public FileRow(string key, long generation, FileEntryState state, long bytes, int pins)
        {
            Key = key;
            Generation = generation;
            State = state;
            Bytes = bytes;
            Pins = pins;
        }
    }

    public sealed class TrimResult
    {
        public long FreedBytes;
        public long RemainingBytes;
        public bool TargetReached;
        public List<LoadError> Errors = new List<LoadError>();
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
