/*
 * datetime     : 2026/2/24
 * description  : Editor 平台图片选择器 (用于编辑器调试)
 *                使用 EditorUtility.OpenFilePanel 模拟图库选择
 *                不支持拍照和裁剪, 约束校验和压缩使用统一的 ImageCompressor
 */

#if UNITY_EDITOR

using System;
using System.IO;
using ToolKit.Tools.ImagePicker;
using UnityEngine;

namespace UnityToolKit.Plugins.ImagePicker
{
    /// <summary>
    /// Editor 平台图片选择器
    /// <para>使用文件选择对话框模拟图库, 便于开发调试</para>
    /// <para>压缩使用 <see cref="ImageCompressor"/> 统一处理, 与移动平台一致</para>
    /// </summary>
    public class EditorImagePicker : MonoBehaviour, IImagePicker
    {
        #region Properties

        /// <summary> Editor 不支持拍照 </summary>
        public bool SupportCamera => false;

        /// <summary> Editor 支持文件选择模拟图库 </summary>
        public bool SupportGallery => true;

        #endregion

        #region Public Methods

        public void PickImage(ImagePickerRequest request, Action<ImagePickerResult> callback)
        {
            if (request == null)
            {
                callback?.Invoke(ImagePickerResult.Fail(EImagePickerError.InvalidRequest));
                return;
            }

            if (request.Source == EImageSource.Camera)
            {
                callback?.Invoke(ImagePickerResult.Fail(EImagePickerError.CameraNotSupported));
                return;
            }

            // 在 Editor 中使用文件对话框选择图片
            string path = UnityEditor.EditorUtility.OpenFilePanel(
                "选择图片", "", "png,jpg,jpeg");

            if (string.IsNullOrEmpty(path))
            {
                callback?.Invoke(ImagePickerResult.Fail(EImagePickerError.Cancelled));
                return;
            }

            try
            {
                ProcessImage(path, request, callback);
            }
            catch (Exception e)
            {
                callback?.Invoke(ImagePickerResult.Fail(EImagePickerError.ProcessFailed, e.Message));
            }
        }

        #endregion

        #region Private Methods

        private void ProcessImage(string sourcePath, ImagePickerRequest request,
            Action<ImagePickerResult> callback)
        {
            // 读取图片
            byte[] imageData = File.ReadAllBytes(sourcePath);
            var texture = new Texture2D(2, 2);
            if (!texture.LoadImage(imageData))
            {
                DestroyImmediate(texture);
                callback?.Invoke(ImagePickerResult.Fail(EImagePickerError.ImageDecodeFailed));
                return;
            }

            int width = texture.width;
            int height = texture.height;
            long fileSize = imageData.Length;

            DestroyImmediate(texture);

            // 约束校验
            if (request.Constraint != null)
            {
                var error = ValidateConstraint(request.Constraint, width, height, fileSize);
                if (error != EImagePickerError.None)
                {
                    callback?.Invoke(ImagePickerResult.Fail(error));
                    return;
                }
            }

            // 构建原始结果
            var result = ImagePickerResult.Succeed(sourcePath, width, height, fileSize);

            // 使用统一压缩器处理 (与移动平台一致)
            if (request.Compress != null && request.Compress.EnableCompress)
            {
                result = ImageCompressor.Compress(result, request.Compress);
            }

            callback?.Invoke(result);
        }

        private EImagePickerError ValidateConstraint(ImageConstraint constraint, int width, int height,
            long fileSize)
        {
            if (constraint.MaxFileSize > 0 && fileSize > constraint.MaxFileSize)
                return EImagePickerError.ConstraintViolation;

            if (constraint.MinWidth > 0 && width < constraint.MinWidth)
                return EImagePickerError.ConstraintViolation;

            if (constraint.MinHeight > 0 && height < constraint.MinHeight)
                return EImagePickerError.ConstraintViolation;

            if (constraint.MaxWidth > 0 && width > constraint.MaxWidth)
                return EImagePickerError.ConstraintViolation;

            if (constraint.MaxHeight > 0 && height > constraint.MaxHeight)
                return EImagePickerError.ConstraintViolation;

            return EImagePickerError.None;
        }

        #endregion
    }
}

#endif
