/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 下载请求 (下载模块自有契约)。承载 URL、请求头与重定向设置；
 *                不引用资源系统的缓存、加载请求或错误类型。请求头原文不进入日志。
 */

using System;
using System.Collections.Generic;

namespace ToolKit.Tools.Network
{
    public sealed class DownloadRequest
    {
        public readonly Uri Url;
        /// <summary> 附加请求头 (如鉴权)；跨主机重定向时由 HttpWebRequest 按其规则处理 </summary>
        public readonly IReadOnlyDictionary<string, string>? Headers;
        public readonly bool AllowRedirects;
        public readonly int MaxRedirects;

        public DownloadRequest(Uri url,
            IReadOnlyDictionary<string, string>? headers = null,
            bool allowRedirects = true,
            int maxRedirects = 5)
        {
            Url = url ?? throw new ArgumentNullException(nameof(url));
            if (url.Scheme != "http" && url.Scheme != "https")
            {
                throw new ArgumentException($"仅支持 http/https: {url.Scheme}", nameof(url));
            }
            Headers = headers;
            AllowRedirects = allowRedirects;
            MaxRedirects = Math.Max(1, maxRedirects);
        }
    }
}
