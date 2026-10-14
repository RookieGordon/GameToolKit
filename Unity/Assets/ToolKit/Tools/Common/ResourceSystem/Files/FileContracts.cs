/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 文件缓存的身份、有效期与配置。身份由业务提供 (来源/路径/版本/变体)，
 *                缓存只按键复用文件，不理解传输协议；运行期不删除已经交付的路径，
 *                只在初始化时按目标容量做一次启动清理。
 */

using System;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 文件身份。相同身份必须对应相同内容；revision 由业务提供 (如 URL 摘要或版本号)，
    /// variant 用于同一路径的多种产出。
    /// </summary>
    public readonly struct FileIdentity : IEquatable<FileIdentity>
    {
        public readonly string Source;
        public readonly string Path;
        public readonly string Revision;
        public readonly string Variant;

        public FileIdentity(string source, string path, string revision, string variant)
        {
            Source = source ?? "";
            Path = path ?? "";
            Revision = revision ?? "";
            Variant = variant ?? "";
        }

        public bool Equals(FileIdentity other)
        {
            return string.Equals(Source, other.Source, StringComparison.Ordinal)
                   && string.Equals(Path, other.Path, StringComparison.Ordinal)
                   && string.Equals(Revision, other.Revision, StringComparison.Ordinal)
                   && string.Equals(Variant, other.Variant, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => obj is FileIdentity other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Source.GetHashCode();
                hash = (hash * 397) ^ Path.GetHashCode();
                hash = (hash * 397) ^ Revision.GetHashCode();
                hash = (hash * 397) ^ Variant.GetHashCode();
                return hash;
            }
        }

        public override string ToString() => Source + ":" + Path + "@" + Revision + "#" + Variant;
    }

    /// <summary> 文件有效期；null 表示不过期 </summary>
    public readonly struct FileValidity
    {
        public readonly DateTimeOffset? ExpiresAt;

        private FileValidity(DateTimeOffset? expiresAt)
        {
            ExpiresAt = expiresAt;
        }

        public static FileValidity Forever => new FileValidity(null);

        public static FileValidity ExpiresAfter(TimeSpan lifetime) =>
            new FileValidity(DateTimeOffset.UtcNow + lifetime);

        public static FileValidity Until(DateTimeOffset moment) => new FileValidity(moment);

        public bool IsExpired(DateTimeOffset now) => ExpiresAt is DateTimeOffset moment && now >= moment;
    }

    /// <summary> 一次缓存请求：身份 + 有效期 </summary>
    public sealed class FileRequest
    {
        public FileRequest(FileIdentity identity, FileValidity? validity = null)
        {
            Identity = identity;
            Validity = validity ?? FileValidity.Forever;
        }

        public FileIdentity Identity { get; }

        public FileValidity Validity { get; }
    }

    /// <summary> 文件缓存配置；启动清理的目标容量，不做运行期硬配额 </summary>
    public sealed class FileCacheOptions
    {
        /// <summary> 缓存根目录；为空时使用临时目录下的默认位置 </summary>
        public string Directory { get; set; } = "";

        /// <summary> 初始化时清理到的目标容量 (字节)；null 表示不清理 </summary>
        public long? StartupTargetBytes { get; set; }
    }
}
