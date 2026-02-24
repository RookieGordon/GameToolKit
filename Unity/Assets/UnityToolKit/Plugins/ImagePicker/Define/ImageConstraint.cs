/*
 * datetime     : 2026/2/24
 * description  : 图片选择约束条件
 *                用于限制哪些图片可以被选中
 */

namespace ToolKit.Tools.ImagePicker
{
    /// <summary>
    /// 图片选择约束条件
    /// <para>可从文件大小、图片尺寸等维度限制可选图片</para>
    /// <para>值为 0 表示不限制该项</para>
    /// </summary>
    public class ImageConstraint
    {
        /// <summary> 最大文件大小 (字节), 0 = 不限制 </summary>
        public long MaxFileSize { get; set; }

        /// <summary> 最小宽度 (像素), 0 = 不限制 </summary>
        public int MinWidth { get; set; }

        /// <summary> 最小高度 (像素), 0 = 不限制 </summary>
        public int MinHeight { get; set; }

        /// <summary> 最大宽度 (像素), 0 = 不限制 </summary>
        public int MaxWidth { get; set; }

        /// <summary> 最大高度 (像素), 0 = 不限制 </summary>
        public int MaxHeight { get; set; }
    }
}
