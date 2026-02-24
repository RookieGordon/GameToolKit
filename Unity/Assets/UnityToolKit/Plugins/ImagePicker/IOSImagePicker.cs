/*
 * datetime     : 2026/2/24
 * description  : iOS 平台图片选择器
 *                通过 P/Invoke 调用 Objective-C 端的 ImagePickerBridge,
 *                实现拍照、图库选择、裁剪功能
 *
 * 选图流程:
 *   1. C# 通过 P/Invoke 调用 ObjC Bridge
 *   2. ObjC 使用 UIImagePickerController (拍照) 或 PHPickerViewController (图库)
 *   3. 需要裁剪时使用 TOCropViewController (开源裁剪库)
 *   4. 通过 UnitySendMessage 回调 C# 返回原始图片
 *   5. C# 侧统一执行压缩 (保证跨平台一致性)
 *
 * 权限:
 *   - 拍照: 系统自动弹出权限对话框 (需 Info.plist 中配置 NSCameraUsageDescription)
 *   - 图库: iOS 14+ 使用 PHPicker 无需权限; 低版本需 NSPhotoLibraryUsageDescription
 *
 * 依赖:
 *   - TOCropViewController 框架 (用于裁剪), 可通过 CocoaPods 集成
 *   - 如不使用裁剪功能, 可不引入 TOCropViewController
 */

#if UNITY_IOS

using System;
using System.Runtime.InteropServices;
using ToolKit.Tools.ImagePicker;
using UnityEngine;

namespace UnityToolKit.Plugins.ImagePicker
{
    /// <summary>
    /// iOS 平台图片选择器
    /// <para>通过 ObjC Bridge 调用 iOS 原生相机/图库, 支持裁剪</para>
    /// <para>压缩在 C# 侧由 <see cref="ImageCompressor"/> 统一处理</para>
    /// </summary>
    public class IOSImagePicker : MonoBehaviour, IImagePicker
    {
        #region Native Methods

        [DllImport("__Internal")]
        private static extern void ToolKit_ImagePicker_Init(string gameObjectName);

        [DllImport("__Internal")]
        private static extern void ToolKit_ImagePicker_PickImage(string configJson);

        [DllImport("__Internal")]
        private static extern bool ToolKit_ImagePicker_IsCameraAvailable();

        #endregion

        #region Fields

        private Action<ImagePickerResult> _callback;
        private ImagePickerRequest _currentRequest;

        #endregion

        #region Properties

        /// <summary> iOS 相机是否可用 (模拟器不支持) </summary>
        public bool SupportCamera => ToolKit_ImagePicker_IsCameraAvailable();

        /// <summary> iOS 支持图库选择 </summary>
        public bool SupportGallery => true;

        #endregion

        #region Public Methods

        /// <summary>
        /// 初始化桥接
        /// </summary>
        public void Init()
        {
            ToolKit_ImagePicker_Init(gameObject.name);
        }

        public void PickImage(ImagePickerRequest request, Action<ImagePickerResult> callback)
        {
            if (request == null)
            {
                callback?.Invoke(ImagePickerResult.Fail(EImagePickerError.InvalidRequest));
                return;
            }

            _callback = callback;
            _currentRequest = request;

            // 不含压缩配置, 压缩在 C# 侧统一处理
            string configJson = BuildConfigJson(request);
            ToolKit_ImagePicker_PickImage(configJson);
        }

        #endregion

        #region Native Callbacks (由 UnitySendMessage 调用)

        /// <summary>
        /// 选图成功回调
        /// <para>参数格式: filePath|width|height|fileSize</para>
        /// <para>收到原始图片后在 C# 侧执行压缩</para>
        /// </summary>
        // ReSharper disable once UnusedMember.Local
        private void OnImagePickerSuccess(string result)
        {
            var parts = result.Split('|');
            if (parts.Length >= 4)
            {
                var pickerResult = ImagePickerResult.Succeed(
                    parts[0],
                    int.Parse(parts[1]),
                    int.Parse(parts[2]),
                    long.Parse(parts[3])
                );

                // 在 C# 侧统一执行压缩
                if (_currentRequest?.Compress != null && _currentRequest.Compress.EnableCompress)
                {
                    pickerResult = ImageCompressor.Compress(pickerResult, _currentRequest.Compress);
                }

                _callback?.Invoke(pickerResult);
            }
            else
            {
                _callback?.Invoke(ImagePickerResult.Fail(EImagePickerError.PlatformError, "Invalid response format"));
            }
            _callback = null;
            _currentRequest = null;
        }

        /// <summary>
        /// 选图失败回调
        /// </summary>
        // ReSharper disable once UnusedMember.Local
        private void OnImagePickerFailed(string errorData)
        {
            ParseNativeError(errorData, out var code, out var detail);
            _callback?.Invoke(ImagePickerResult.Fail(code, detail));
            _callback = null;
            _currentRequest = null;
        }

        /// <summary>
        /// 用户取消选图回调
        /// </summary>
        // ReSharper disable once UnusedMember.Local
        private void OnImagePickerCancelled(string _)
        {
            _callback?.Invoke(ImagePickerResult.Fail(EImagePickerError.Cancelled));
            _callback = null;
            _currentRequest = null;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 解析 Native 层发送的错误数据
        /// <para>格式: "错误码" 或 "错误码|详情"</para>
        /// </summary>
        private static void ParseNativeError(string errorData, out EImagePickerError code,
            out string detail)
        {
            detail = null;
            code = EImagePickerError.PlatformError;

            if (string.IsNullOrEmpty(errorData)) return;

            var parts = errorData.Split(new[] { '|' }, 2);
            if (int.TryParse(parts[0], out int codeInt))
            {
                code = (EImagePickerError)codeInt;
                if (parts.Length > 1)
                    detail = parts[1];
            }
            else
            {
                // 兼容: 如果 Native 发送的不是错误码格式, 当作详情保留
                detail = errorData;
            }
        }

        /// <summary>
        /// 将 ImagePickerRequest 序列化为 JSON 字符串传递给 ObjC 端
        /// <para>不含压缩配置, 压缩在 C# 侧统一处理</para>
        /// </summary>
        private string BuildConfigJson(ImagePickerRequest request)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{");

            sb.AppendFormat("\"source\":{0}", (int)request.Source);

            if (request.Constraint != null)
            {
                var c = request.Constraint;
                sb.AppendFormat(",\"maxFileSize\":{0}", c.MaxFileSize);
                sb.AppendFormat(",\"minWidth\":{0}", c.MinWidth);
                sb.AppendFormat(",\"minHeight\":{0}", c.MinHeight);
                sb.AppendFormat(",\"maxWidth\":{0}", c.MaxWidth);
                sb.AppendFormat(",\"maxHeight\":{0}", c.MaxHeight);
            }

            if (request.Crop != null && request.Crop.EnableCrop)
            {
                var crop = request.Crop;
                sb.Append(",\"enableCrop\":true");
                sb.AppendFormat(",\"cropShape\":{0}", (int)crop.Shape);
                sb.AppendFormat(",\"aspectRatioX\":{0}", crop.AspectRatioX);
                sb.AppendFormat(",\"aspectRatioY\":{0}", crop.AspectRatioY);
                sb.AppendFormat(",\"maxOutputWidth\":{0}", crop.MaxOutputWidth);
                sb.AppendFormat(",\"maxOutputHeight\":{0}", crop.MaxOutputHeight);
            }
            else
            {
                sb.Append(",\"enableCrop\":false");
            }

            sb.Append("}");
            return sb.ToString();
        }

        #endregion
    }
}

#endif
