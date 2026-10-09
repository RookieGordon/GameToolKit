/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 远端文件加载器的请求组合 (HTTP 解耦版)。Cache 是纯缓存需求，
 *                Download 是下载模块请求；加载器负责连接两者。不放在 Files——
 *                纯缓存契约不通过这个组合间接接收网络参数。
 */

using System.Collections.Generic;
using ToolKit.Tools.Network;

namespace ToolKit.Tools.Common
{
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
        public RemoteFileRequest(FileIdentity identity, FileValidity validity,
            System.Uri url, IReadOnlyDictionary<string, string>? headers = null,
            bool allowRedirects = true, int maxRedirects = 5,
            long? expectedLength = null, string? expectedSha256 = null)
        {
            Cache = new FileRequest(identity, validity, expectedLength, expectedSha256);
            Download = new DownloadRequest(url, headers, allowRedirects, maxRedirects);
        }
    }
}
