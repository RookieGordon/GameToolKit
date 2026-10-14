/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 文件加载器共用的请求载荷。RequestOptions.Parameters 传入 FileLoadParameters；
 *                ConversionKey 是影响转换结果的稳定参数键，参与资源身份并原样交给转换器。
 *                转换器由目标类型唯一确定；不支持的参数类型在解析阶段明确报错。
 */

using System.Collections.Generic;
using ToolKit.Tools.Network;

namespace ToolKit.Tools.Common
{
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
            Cache = cache ?? throw new System.ArgumentNullException(nameof(cache));
            Download = download ?? throw new System.ArgumentNullException(nameof(download));
        }

        /// <summary> 便捷构造：同 URL 的缓存身份与下载请求 </summary>
        public RemoteFileRequest(FileIdentity identity, FileValidity validity, System.Uri url,
            IReadOnlyDictionary<string, string>? headers = null,
            bool allowRedirects = true, int maxRedirects = 5)
        {
            Cache = new FileRequest(identity, validity);
            Download = new DownloadRequest(url, headers, allowRedirects, maxRedirects);
        }
    }
}
