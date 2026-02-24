/*
 * datetime     : 2026/2/24
 * description  : 图片压缩配置
 */

namespace ToolKit.Tools.ImagePicker
{
    /// <summary>
    /// 图片压缩配置
    /// <para>支持尺寸压缩 (等比缩放) 和质量压缩 (JPEG 质量)</para>
    /// </summary>
    public class CompressConfig
    {
        /// <summary> 是否启用压缩 </summary>
        public bool EnableCompress { get; set; }

        /// <summary> 压缩后最大宽度 (像素), 0 = 不限制 </summary>
        public int MaxWidth { get; set; }

        /// <summary> 压缩后最大高度 (像素), 0 = 不限制 </summary>
        public int MaxHeight { get; set; }

        /// <summary>
        /// JPEG 压缩质量 (0~100)
        /// <para>100 = 最佳质量, 0 = 最大压缩, 默认 85</para>
        /// </summary>
        public int Quality { get; set; } = 85;

        /// <summary>
        /// 目标最大文件大小 (字节), 0 = 不限制
        /// <para>引擎会循环降低质量直到文件大小满足该限制</para>
        /// </summary>
        public long MaxFileSize { get; set; }
    }
}
