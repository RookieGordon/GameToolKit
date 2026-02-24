/*
 * datetime     : 2026/2/24
 * description  : 图片选择器辅助工具
 *                提供从选图结果加载 Texture2D、Sprite 等便捷方法
 */

using System;
using System.IO;
using ToolKit.Tools.ImagePicker;
using UnityEngine;

namespace UnityToolKit.Plugins.ImagePicker
{
    /// <summary>
    /// 图片选择器辅助工具
    /// <para>提供常用的快捷方法, 简化选图流程</para>
    /// </summary>
    public static class ImagePickerHelper
    {
        /// <summary>
        /// 从选图结果加载 Texture2D
        /// </summary>
        /// <param name="result">选图结果</param>
        /// <returns>加载的 Texture2D, 失败返回 null</returns>
        public static Texture2D LoadTexture(ImagePickerResult result)
        {
            if (result == null || !result.Success || string.IsNullOrEmpty(result.FilePath))
                return null;

            return LoadTextureFromFile(result.FilePath);
        }

        /// <summary>
        /// 从文件路径加载 Texture2D
        /// </summary>
        /// <param name="filePath">图片文件路径</param>
        /// <returns>加载的 Texture2D, 失败返回 null</returns>
        public static Texture2D LoadTextureFromFile(string filePath)
        {
            if (!File.Exists(filePath)) return null;

            try
            {
                byte[] data = File.ReadAllBytes(filePath);
                var texture = new Texture2D(2, 2);
                if (texture.LoadImage(data))
                    return texture;

                UnityEngine.Object.Destroy(texture);
                return null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ImagePicker] 加载图片失败: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从选图结果创建 Sprite
        /// </summary>
        /// <param name="result">选图结果</param>
        /// <param name="pixelsPerUnit">每单位像素数, 默认 100</param>
        /// <returns>创建的 Sprite, 失败返回 null</returns>
        public static Sprite LoadSprite(ImagePickerResult result, float pixelsPerUnit = 100f)
        {
            var texture = LoadTexture(result);
            if (texture == null) return null;

            return Sprite.Create(
                texture,
                new Rect(0, 0, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit
            );
        }

        /// <summary>
        /// 选图并自动加载为 Texture2D (一步到位的便捷方法)
        /// </summary>
        /// <param name="request">选择请求参数</param>
        /// <param name="callback">
        /// 回调, 成功时 texture 非 null, error 为 None
        /// <para>失败时 texture 为 null, error 为对应错误码</para>
        /// </param>
        public static void PickAndLoadTexture(ImagePickerRequest request,
            Action<Texture2D, EImagePickerError> callback)
        {
            var picker = ImagePickerFactory.Get();
            if (picker == null)
            {
                callback?.Invoke(null, EImagePickerError.PlatformNotSupported);
                return;
            }

            picker.PickImage(request, result =>
            {
                if (!result.Success)
                {
                    callback?.Invoke(null, result.ErrorCode);
                    return;
                }

                var texture = LoadTexture(result);
                if (texture == null)
                {
                    callback?.Invoke(null, EImagePickerError.ImageDecodeFailed);
                    return;
                }

                callback?.Invoke(texture, EImagePickerError.None);
            });
        }

        /// <summary>
        /// 选图并自动加载为 Sprite (一步到位的便捷方法)
        /// </summary>
        /// <param name="request">选择请求参数</param>
        /// <param name="callback">
        /// 回调, 成功时 sprite 非 null, error 为 None
        /// </param>
        /// <param name="pixelsPerUnit">每单位像素数, 默认 100</param>
        public static void PickAndLoadSprite(ImagePickerRequest request,
            Action<Sprite, EImagePickerError> callback, float pixelsPerUnit = 100f)
        {
            var picker = ImagePickerFactory.Get();
            if (picker == null)
            {
                callback?.Invoke(null, EImagePickerError.PlatformNotSupported);
                return;
            }

            picker.PickImage(request, result =>
            {
                if (!result.Success)
                {
                    callback?.Invoke(null, result.ErrorCode);
                    return;
                }

                var sprite = LoadSprite(result, pixelsPerUnit);
                if (sprite == null)
                {
                    callback?.Invoke(null, EImagePickerError.ImageDecodeFailed);
                    return;
                }

                callback?.Invoke(sprite, EImagePickerError.None);
            });
        }

        /// <summary>
        /// 清理 ImagePicker 的临时文件
        /// <para>建议在不需要缓存图片时调用</para>
        /// </summary>
        public static void CleanupTempFiles()
        {
            try
            {
                string dir = Path.Combine(Application.temporaryCachePath, "ImagePicker");
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ImagePicker] 清理临时文件失败: {e.Message}");
            }
        }
    }
}
