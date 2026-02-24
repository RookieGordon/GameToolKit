/*
 * datetime     : 2026/2/24
 * description  : 图片选择请求参数
 *                封装一次选图/拍照操作的所有配置
 */

namespace ToolKit.Tools.ImagePicker
{
    /// <summary>
    /// 图片选择请求
    /// <para>包含图片来源、选择约束、裁剪配置、压缩配置等全部参数</para>
    /// </summary>
    public class ImagePickerRequest
    {
        /// <summary>
        /// 图片来源 (相册或拍照)
        /// <para>默认从相册选择</para>
        /// </summary>
        public EImageSource Source { get; set; } = EImageSource.Gallery;

        /// <summary>
        /// 图片选择约束条件
        /// <para>为 null 时不限制</para>
        /// </summary>
        public ImageConstraint Constraint { get; set; }

        /// <summary>
        /// 裁剪配置
        /// <para>为 null 或 <see cref="CropConfig.EnableCrop"/> 为 false 时不裁剪</para>
        /// </summary>
        public CropConfig Crop { get; set; }

        /// <summary>
        /// 压缩配置
        /// <para>为 null 或 <see cref="CompressConfig.EnableCompress"/> 为 false 时不压缩</para>
        /// </summary>
        public CompressConfig Compress { get; set; }
    }
}
