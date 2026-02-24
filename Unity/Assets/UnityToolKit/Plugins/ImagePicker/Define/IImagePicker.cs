/*
 * datetime     : 2026/2/24
 * description  : 图片选择器平台接口
 *                定义跨平台的选图/拍照功能抽象
 *                具体实现在 UnityToolKit/Plugins/ImagePicker 下面
 */

using System;

namespace ToolKit.Tools.ImagePicker
{
    /// <summary>
    /// 图片选择器平台接口
    /// <para>调用移动平台的拍照和图库功能，选择图片并返回处理后的结果</para>
    /// <para>支持图片约束校验、裁剪、压缩等功能</para>
    /// </summary>
    public interface IImagePicker
    {
        /// <summary> 当前平台是否支持拍照 </summary>
        bool SupportCamera { get; }

        /// <summary> 当前平台是否支持图库选择 </summary>
        bool SupportGallery { get; }

        /// <summary>
        /// 选择图片
        /// <para>根据 request 中的配置调用平台原生界面进行选图/拍照</para>
        /// <para>选择完成后依次执行约束校验 → 裁剪 → 压缩, 最后通过 callback 返回结果</para>
        /// </summary>
        /// <param name="request">选择请求参数</param>
        /// <param name="callback">
        /// 结果回调, 在主线程调用
        /// <para>成功时 <see cref="ImagePickerResult.Success"/> 为 true,
        /// <see cref="ImagePickerResult.FilePath"/> 包含处理后的图片路径</para>
        /// <para>用户取消或出错时 <see cref="ImagePickerResult.Success"/> 为 false</para>
        /// </param>
        void PickImage(ImagePickerRequest request, Action<ImagePickerResult> callback);
    }
}
