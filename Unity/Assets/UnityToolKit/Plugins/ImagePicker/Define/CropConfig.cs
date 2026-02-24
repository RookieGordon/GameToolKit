/*
 * datetime     : 2026/2/24
 * description  : 裁剪配置
 */

namespace ToolKit.Tools.ImagePicker
{
    /// <summary>
    /// 图片裁剪配置
    /// <para>控制裁剪形状、宽高比、输出尺寸等</para>
    /// </summary>
    public class CropConfig
    {
        /// <summary> 是否启用裁剪 </summary>
        public bool EnableCrop { get; set; }

        /// <summary>
        /// 裁剪区域形状
        /// <para>默认为矩形</para>
        /// </summary>
        public ECropShape Shape { get; set; } = ECropShape.Rectangle;

        /// <summary>
        /// 宽高比 X 分量, 0 = 自由裁剪
        /// <para>例: AspectRatioX=1, AspectRatioY=1 表示 1:1 正方形裁剪</para>
        /// </summary>
        public float AspectRatioX { get; set; }

        /// <summary>
        /// 宽高比 Y 分量, 0 = 自由裁剪
        /// </summary>
        public float AspectRatioY { get; set; }

        /// <summary> 裁剪输出的最大宽度 (像素), 0 = 不限制 </summary>
        public int MaxOutputWidth { get; set; }

        /// <summary> 裁剪输出的最大高度 (像素), 0 = 不限制 </summary>
        public int MaxOutputHeight { get; set; }
    }
}
