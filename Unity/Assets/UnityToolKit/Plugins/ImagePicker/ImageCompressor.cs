/*
 * datetime     : 2026/2/24
 * description  : 跨平台统一图片压缩器
 *                在 Unity C# 侧统一处理图片压缩 (尺寸缩放 + 质量压缩)
 *                保证 Android / iOS / Editor 压缩结果一致
 */

using System;
using System.IO;
using ToolKit.Tools.ImagePicker;
using UnityEngine;

namespace UnityToolKit.Plugins.ImagePicker
{
    /// <summary>
    /// 跨平台统一图片压缩器
    /// <para>在 Unity 侧统一执行压缩, 保证不同平台的压缩结果一致</para>
    /// </summary>
    public static class ImageCompressor
    {
        /// <summary>
        /// 对选图结果执行压缩处理
        /// </summary>
        /// <param name="result">原始选图结果 (非压缩)</param>
        /// <param name="config">压缩配置</param>
        /// <returns>压缩后的结果, 失败时 Success=false</returns>
        public static ImagePickerResult Compress(ImagePickerResult result, CompressConfig config)
        {
            if (result == null || !result.Success)
                return result;

            if (config == null || !config.EnableCompress)
                return result;

            if (string.IsNullOrEmpty(result.FilePath) || !File.Exists(result.FilePath))
                return ImagePickerResult.Fail(EImagePickerError.ImageNotFound);

            try
            {
                byte[] imageData = File.ReadAllBytes(result.FilePath);
                var texture = new Texture2D(2, 2);
                if (!texture.LoadImage(imageData))
                {
                    UnityEngine.Object.Destroy(texture);
                    return ImagePickerResult.Fail(EImagePickerError.ImageDecodeFailed);
                }

                // 执行压缩
                var compressedData = CompressTexture(texture, config);
                UnityEngine.Object.Destroy(texture);

                // 保存到临时文件
                string dir = Path.Combine(Application.temporaryCachePath, "ImagePicker");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string outputPath = Path.Combine(dir,
                    $"compressed_{DateTime.Now:yyyyMMdd_HHmmss}_{UnityEngine.Random.Range(0, 9999)}.jpg");
                File.WriteAllBytes(outputPath, compressedData.Data);

                return ImagePickerResult.Succeed(
                    outputPath,
                    compressedData.Width,
                    compressedData.Height,
                    compressedData.Data.Length
                );
            }
            catch (Exception e)
            {
                return ImagePickerResult.Fail(EImagePickerError.CompressFailed, e.Message);
            }
        }

        /// <summary>
        /// 对 Texture2D 执行压缩, 返回 JPEG 数据
        /// </summary>
        public static CompressedImageData CompressTexture(Texture2D source, CompressConfig config)
        {
            int targetWidth = source.width;
            int targetHeight = source.height;

            // 1. 尺寸缩放 (等比)
            if (config.MaxWidth > 0 && targetWidth > config.MaxWidth)
            {
                float ratio = (float)config.MaxWidth / targetWidth;
                targetWidth = config.MaxWidth;
                targetHeight = Mathf.RoundToInt(targetHeight * ratio);
            }
            if (config.MaxHeight > 0 && targetHeight > config.MaxHeight)
            {
                float ratio = (float)config.MaxHeight / targetHeight;
                targetHeight = config.MaxHeight;
                targetWidth = Mathf.RoundToInt(targetWidth * ratio);
            }

            // 确保最小尺寸
            targetWidth = Mathf.Max(targetWidth, 1);
            targetHeight = Mathf.Max(targetHeight, 1);

            // 2. 缩放纹理
            Texture2D resized = source;
            bool needCleanup = false;
            if (targetWidth != source.width || targetHeight != source.height)
            {
                resized = ScaleTexture(source, targetWidth, targetHeight);
                needCleanup = true;
            }

            // 3. 质量压缩
            int quality = config.Quality > 0 ? config.Quality : 85;
            byte[] jpgData = resized.EncodeToJPG(quality);

            // 4. 循环降低质量直到满足文件大小限制
            if (config.MaxFileSize > 0)
            {
                while (jpgData.Length > config.MaxFileSize && quality > 10)
                {
                    quality -= 5;
                    jpgData = resized.EncodeToJPG(quality);
                }
            }

            int finalWidth = resized.width;
            int finalHeight = resized.height;

            if (needCleanup)
                UnityEngine.Object.Destroy(resized);

            return new CompressedImageData
            {
                Data = jpgData,
                Width = finalWidth,
                Height = finalHeight
            };
        }

        /// <summary>
        /// 缩放纹理
        /// </summary>
        private static Texture2D ScaleTexture(Texture2D source, int targetWidth, int targetHeight)
        {
            var rt = RenderTexture.GetTemporary(targetWidth, targetHeight);
            RenderTexture.active = rt;
            Graphics.Blit(source, rt);

            var result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
            result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            result.Apply();

            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);

            return result;
        }

        /// <summary>
        /// 压缩后的图片数据
        /// </summary>
        public struct CompressedImageData
        {
            public byte[] Data;
            public int Width;
            public int Height;
        }
    }
}
