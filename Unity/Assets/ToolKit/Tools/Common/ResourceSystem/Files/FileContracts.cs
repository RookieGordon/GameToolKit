/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 文件缓存的身份、有效期、请求与配置，以及本地/远程文件加载参数。
 *                身份由业务提供 (来源/路径/版本/变体)，缓存只按键复用文件，不理解传输协议。
 *                有效期只决定"这份文件还算不算新", 与磁盘删除策略分开：标记过期不等于完成回收。
 */

using System;
using System.Collections.Generic;
using ToolKit.Tools.Network;

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

        /// <summary> 仅供展示与日志；身份比较与缓存键都使用结构化字段，不依赖本字符串 </summary>
        public override string ToString() => Source + ":" + Path + "@" + Revision + "#" + Variant;
    }

    /// <summary>
    /// 文件有效期；null 表示不过期。业务注入的映射应为每次请求给出期限，
    /// 不能在加载器构造时算一次然后复用 (否则期限一到所有请求都不再命中，重建加载器又给旧文件新的信任期)。
    /// </summary>
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

    /// <summary>
    /// 文件缓存配置；启动清理的目标容量，不做运行期硬配额。
    /// <b>启用文件缓存时必须给出有效目标</b>：没有目标就没有实际生效的删除策略。
    /// </summary>
    public sealed class FileCacheOptions
    {
        /// <summary> 缓存根目录；为空时使用临时目录下的默认位置 </summary>
        public string Directory { get; set; } = "";

        /// <summary> 初始化时清理到的目标容量 (字节)；必须为正，null 或非正会被初始化拒绝 </summary>
        public long? StartupTargetBytes { get; set; }
    }

    /// <summary>
    /// 一条隔离记录：清理失败的文件被移到隔离区后的实际位置与原因。
    /// 它只陈述事实——文件仍然存在、仍由隔离区负责，不表示"已回收完成"。
    /// </summary>
    public sealed class QuarantinedFile
    {
        public QuarantinedFile(string originalPath, string quarantinedPath, string reason)
        {
            OriginalPath = originalPath ?? "";
            QuarantinedPath = quarantinedPath ?? "";
            Reason = reason ?? "";
        }

        /// <summary> 隔离前的位置（未成功移动时为空）</summary>
        public string OriginalPath { get; }

        /// <summary> 隔离后的实际位置</summary>
        public string QuarantinedPath { get; }

        /// <summary> 触发隔离的原因（原始失败信息）</summary>
        public string Reason { get; }
    }

    /// <summary>
    /// 文件加载器共用的请求载荷。RequestOptions.Parameters 传入 FileLoadParameters；
    /// ConversionKey 是影响转换结果的稳定参数键，参与资源身份并原样交给转换器。
    /// 转换器由目标类型唯一确定；不支持的参数类型在解析阶段明确报错。
    /// </summary>
    public sealed class FileLoadParameters
    {
        public readonly string? ConversionKey;

        public FileLoadParameters(string? conversionKey = null)
        {
            // 空字符串与未传参数使用同一身份，也给转换器传入同一个值。
            ConversionKey = string.IsNullOrEmpty(conversionKey) ? null : conversionKey;
        }

        internal static string? GetConversionKey(object? parameters)
        {
            switch (parameters)
            {
                case null:
                    return null;
                case FileLoadParameters typed:
                    return typed.ConversionKey;
                default:
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                        new Dictionary<string, object>
                        {
                            { "parametersType", parameters.GetType().Name },
                            { "reason", "Parameters 须为 FileLoadParameters 或 null" },
                        }));
            }
        }
    }

    /// <summary> 远端文件请求：纯缓存身份 + 下载模块请求；加载器负责把两者接起来 </summary>
    public sealed class RemoteFileRequest
    {
        public readonly FileRequest Cache;
        public readonly DownloadRequest Download;

        public RemoteFileRequest(FileRequest cache, DownloadRequest download)
        {
            Cache = cache ?? throw new ArgumentNullException(nameof(cache));
            Download = download ?? throw new ArgumentNullException(nameof(download));
        }

        /// <summary> 便捷构造：同 URL 的缓存身份与下载请求 </summary>
        public RemoteFileRequest(FileIdentity identity, FileValidity validity, Uri url,
            IReadOnlyDictionary<string, string>? headers = null,
            bool allowRedirects = true, int maxRedirects = 5)
        {
            Cache = new FileRequest(identity, validity);
            Download = new DownloadRequest(url, headers, allowRedirects, maxRedirects);
        }
    }
}
