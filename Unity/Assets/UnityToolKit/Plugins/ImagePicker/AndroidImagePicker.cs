/*
 * datetime     : 2026/2/24
 * description  : Android 平台图片选择器
 *                通过 AndroidJavaClass 调用 Java 端的 ImagePickerBridge,
 *                实现拍照、图库选择、裁剪功能
 *
 * 选图流程:
 *   1. C# 调用 Java Bridge 打开相机/图库
 *   2. Java 端处理权限请求 (相机权限/存储权限)
 *   3. Java 端获取图片后进行约束校验
 *   4. 需要裁剪时跳转 uCrop (开源裁剪库)
 *   5. 通过 UnitySendMessage 回调 C# 返回原始图片
 *   6. C# 侧统一执行压缩 (保证跨平台一致性)
 *
 * 依赖:
 *   - uCrop 库 (Yalantis/uCrop) 的 AAR 需要放在 Plugins/Android/ 下
 *   - 如不使用裁剪功能, 可不引入 uCrop
 */

#if UNITY_ANDROID

using System;
using ToolKit.Tools.ImagePicker;
using UnityEngine;

namespace UnityToolKit.Plugins.ImagePicker
{
    /// <summary>
    /// Android 平台图片选择器
    /// <para>通过 Java Bridge 调用 Android 原生相机/图库, 支持 uCrop 裁剪</para>
    /// <para>压缩在 C# 侧由 <see cref="ImageCompressor"/> 统一处理</para>
    /// </summary>
    public class AndroidImagePicker : MonoBehaviour, IImagePicker
    {
        #region Fields

        private static readonly string BridgeClassName = "com.toolkit.imagepicker.ImagePickerBridge";

        private AndroidJavaClass _bridge;
        private bool _initialized;
        private Action<ImagePickerResult> _callback;
        private ImagePickerRequest _currentRequest;

        #endregion

        #region Properties

        /// <summary> Android 支持拍照 </summary>
        public bool SupportCamera => true;

        /// <summary> Android 支持图库选择 </summary>
        public bool SupportGallery => true;

        #endregion

        #region Public Methods

        /// <summary>
        /// 初始化桥接
        /// <para>必须在 Unity 主线程调用</para>
        /// </summary>
        public void Init()
        {
            if (_initialized) return;

            _bridge = new AndroidJavaClass(BridgeClassName);

            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                _bridge.CallStatic("init", activity, gameObject.name);
            }

            _initialized = true;
        }

        public void PickImage(ImagePickerRequest request, Action<ImagePickerResult> callback)
        {
            if (request == null)
            {
                callback?.Invoke(ImagePickerResult.Fail(EImagePickerError.InvalidRequest));
                return;
            }

            EnsureInitialized();

            _callback = callback;
            _currentRequest = request;

            // 构造 JSON 配置传递给 Java 端 (不含压缩配置, 压缩在 C# 侧处理)
            string configJson = BuildConfigJson(request);
            _bridge.CallStatic("pickImage", configJson);
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

        private void EnsureInitialized()
        {
            if (!_initialized) Init();
        }

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
        /// 将 ImagePickerRequest 序列化为 JSON 字符串传递给 Java 端
        /// <para>不含压缩配置, 压缩在 C# 侧统一处理</para>
        /// </summary>
        private string BuildConfigJson(ImagePickerRequest request)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{");

            // 图片来源
            sb.AppendFormat("\"source\":{0}", (int)request.Source);

            // 约束条件
            if (request.Constraint != null)
            {
                var c = request.Constraint;
                sb.AppendFormat(",\"maxFileSize\":{0}", c.MaxFileSize);
                sb.AppendFormat(",\"minWidth\":{0}", c.MinWidth);
                sb.AppendFormat(",\"minHeight\":{0}", c.MinHeight);
                sb.AppendFormat(",\"maxWidth\":{0}", c.MaxWidth);
                sb.AppendFormat(",\"maxHeight\":{0}", c.MaxHeight);
            }

            // 裁剪配置
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
