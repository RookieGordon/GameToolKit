using System;
using System.Text;
using ToolKit.Tools.ImagePicker;
using UnityEngine;
using UnityEngine.UI;
using UnityToolKit.Plugins.ImagePicker;

namespace Tests
{
    public class ImagePickTest : MonoBehaviour
    {
        public RawImage DisplayImage;
        public Button TakePhotoBtn;
        public Button PickFromGalleryBtn;
        public Button ConstraintPresetBtn;
        public Button CropPresetBtn;
        public Button CompressPresetBtn;
        public Button ClearPreviewBtn;
        public Button CleanupCacheBtn;
        public Text StatusText;
        public Text CapabilityText;
        public Text RequestText;
        public Text ResultText;

        private Texture2D _loadedTexture;
        private AspectRatioFitter _previewFitter;
        private EImageSource _previewSource = EImageSource.Gallery;
        private ConstraintPreset _constraintPreset;
        private CropPreset _cropPreset;
        private CompressPreset _compressPreset = CompressPreset.Upload1024;

        private enum ConstraintPreset
        {
            Off,
            Avatar,
            LargeFileGuard
        }

        private enum CropPreset
        {
            Off,
            Square512,
            Circle256
        }

        private enum CompressPreset
        {
            Off,
            Upload1024,
            Avatar256
        }

        private void Awake()
        {
            BindButtons();
            RefreshPresetLabels();
            RefreshPlatformState();
            RefreshRequestPreview();
            SetResultText("等待测试操作");
        }

        private void BindButtons()
        {
            BindButton(TakePhotoBtn, OnClickTakePhotoBtn);
            BindButton(PickFromGalleryBtn, OnClickPickFromGalleryBtn);
            BindButton(ConstraintPresetBtn, OnClickConstraintPresetBtn);
            BindButton(CropPresetBtn, OnClickCropPresetBtn);
            BindButton(CompressPresetBtn, OnClickCompressPresetBtn);
            BindButton(ClearPreviewBtn, ClearPreview);
            BindButton(CleanupCacheBtn, CleanupTempFiles);
        }

        private void BindButton(Button button, UnityEngine.Events.UnityAction callback)
        {
            if (button == null)
                return;

            button.onClick.RemoveListener(callback);
            button.onClick.AddListener(callback);
        }

        private void UnbindButton(Button button, UnityEngine.Events.UnityAction callback)
        {
            if (button != null)
                button.onClick.RemoveListener(callback);
        }

        private void RefreshPlatformState()
        {
            var picker = ImagePickerFactory.Get();
            bool supportGallery = picker != null && picker.SupportGallery;
            bool supportCamera = picker != null && picker.SupportCamera;

            if (PickFromGalleryBtn != null)
                PickFromGalleryBtn.interactable = supportGallery;

            if (TakePhotoBtn != null)
                TakePhotoBtn.interactable = supportCamera;

            if (CapabilityText != null)
            {
                CapabilityText.text = $"平台能力: 图库 {(supportGallery ? "可用" : "不可用")} / 相机 {(supportCamera ? "可用" : "不可用")}";
            }

            if (picker == null)
            {
                SetStatus("当前平台未找到可用的 ImagePicker 实现", true);
                return;
            }

#if UNITY_EDITOR
            SetStatus(supportCamera ? "Editor 模式: 可测试拍照与图库" : "Editor 模式: 仅支持图库选图", !supportCamera);
#else
            SetStatus("请选择拍照或图库流程开始测试");
#endif
        }

        private void OnClickTakePhotoBtn()
        {
            StartPick(EImageSource.Camera);
        }

        private void OnClickPickFromGalleryBtn()
        {
            StartPick(EImageSource.Gallery);
        }

        private void OnClickConstraintPresetBtn()
        {
            _constraintPreset = NextPreset(_constraintPreset, ConstraintPreset.LargeFileGuard);
            RefreshPresetLabels();
            RefreshRequestPreview();
        }

        private void OnClickCropPresetBtn()
        {
            _cropPreset = NextPreset(_cropPreset, CropPreset.Circle256);
            RefreshPresetLabels();
            RefreshRequestPreview();
        }

        private void OnClickCompressPresetBtn()
        {
            _compressPreset = NextPreset(_compressPreset, CompressPreset.Avatar256);
            RefreshPresetLabels();
            RefreshRequestPreview();
        }

        private T NextPreset<T>(T current, T max) where T : struct, Enum
        {
            int next = (Convert.ToInt32(current) + 1) % (Convert.ToInt32(max) + 1);
            return (T)Enum.ToObject(typeof(T), next);
        }

        private void RefreshPresetLabels()
        {
            SetButtonLabel(ConstraintPresetBtn, GetConstraintLabel());
            SetButtonLabel(CropPresetBtn, GetCropLabel());
            SetButtonLabel(CompressPresetBtn, GetCompressLabel());
        }

        private void SetButtonLabel(Button button, string label)
        {
            if (button == null)
                return;

            var text = button.GetComponentInChildren<Text>();
            if (text != null)
                text.text = label;
        }

        private string GetConstraintLabel()
        {
            switch (_constraintPreset)
            {
                case ConstraintPreset.Avatar:
                    return "约束: 头像图";
                case ConstraintPreset.LargeFileGuard:
                    return "约束: 大图保护";
                default:
                    return "约束: 关闭";
            }
        }

        private string GetCropLabel()
        {
            switch (_cropPreset)
            {
                case CropPreset.Square512:
                    return "裁剪: 方形 512";
                case CropPreset.Circle256:
                    return "裁剪: 圆形 256";
                default:
                    return "裁剪: 关闭";
            }
        }

        private string GetCompressLabel()
        {
            switch (_compressPreset)
            {
                case CompressPreset.Upload1024:
                    return "压缩: 上传 1024";
                case CompressPreset.Avatar256:
                    return "压缩: 头像 256";
                default:
                    return "压缩: 原图";
            }
        }

        private void StartPick(EImageSource source)
        {
            _previewSource = source;
            var picker = ImagePickerFactory.Get();
            if (picker == null)
            {
                SetStatus("当前平台不支持 ImagePicker", true);
                SetResultText("平台不支持 ImagePicker");
                return;
            }

            if (source == EImageSource.Camera && !picker.SupportCamera)
            {
                SetStatus("当前平台不支持拍照", true);
                SetResultText("拍照能力不可用");
                return;
            }

            if (source == EImageSource.Gallery && !picker.SupportGallery)
            {
                SetStatus("当前平台不支持图库选图", true);
                SetResultText("图库能力不可用");
                return;
            }

            var request = BuildRequest(source);
            SetRequestText(request);
            SetStatus(source == EImageSource.Camera ? "正在打开相机..." : "正在打开图库...");
            picker.PickImage(request, HandlePickResult);
        }

        private ImagePickerRequest BuildRequest(EImageSource source)
        {
            return new ImagePickerRequest
            {
                Source = source,
                Constraint = BuildConstraint(),
                Crop = BuildCrop(),
                Compress = BuildCompress()
            };
        }

        private ImageConstraint BuildConstraint()
        {
            switch (_constraintPreset)
            {
                case ConstraintPreset.Avatar:
                    return new ImageConstraint
                    {
                        MinWidth = 200,
                        MinHeight = 200,
                        MaxFileSize = 2 * 1024 * 1024
                    };
                case ConstraintPreset.LargeFileGuard:
                    return new ImageConstraint
                    {
                        MaxWidth = 4096,
                        MaxHeight = 4096,
                        MaxFileSize = 10 * 1024 * 1024
                    };
                default:
                    return null;
            }
        }

        private CropConfig BuildCrop()
        {
            switch (_cropPreset)
            {
                case CropPreset.Square512:
                    return new CropConfig
                    {
                        EnableCrop = true,
                        Shape = ECropShape.Rectangle,
                        AspectRatioX = 1,
                        AspectRatioY = 1,
                        MaxOutputWidth = 512,
                        MaxOutputHeight = 512
                    };
                case CropPreset.Circle256:
                    return new CropConfig
                    {
                        EnableCrop = true,
                        Shape = ECropShape.Circle,
                        AspectRatioX = 1,
                        AspectRatioY = 1,
                        MaxOutputWidth = 256,
                        MaxOutputHeight = 256
                    };
                default:
                    return null;
            }
        }

        private CompressConfig BuildCompress()
        {
            switch (_compressPreset)
            {
                case CompressPreset.Upload1024:
                    return new CompressConfig
                    {
                        EnableCompress = true,
                        MaxWidth = 1024,
                        MaxHeight = 1024,
                        Quality = 85,
                        MaxFileSize = 512 * 1024
                    };
                case CompressPreset.Avatar256:
                    return new CompressConfig
                    {
                        EnableCompress = true,
                        MaxWidth = 256,
                        MaxHeight = 256,
                        Quality = 85,
                        MaxFileSize = 128 * 1024
                    };
                default:
                    return null;
            }
        }

        private void HandlePickResult(ImagePickerResult result)
        {
            if (result == null)
            {
                SetStatus("结果为空", true);
                SetResultText("ImagePicker 回调返回了空结果");
                return;
            }

            if (!result.Success)
            {
                string error = $"失败: {result.ErrorCode}";
                if (!string.IsNullOrEmpty(result.ErrorDetail))
                    error = $"{error}\n详情: {result.ErrorDetail}";

                SetStatus(error, true);
                SetResultText(error);
                return;
            }

            var texture = ImagePickerHelper.LoadTexture(result);
            if (texture == null)
            {
                SetStatus("图片解码失败", true);
                SetResultText("选图成功，但本地加载 Texture2D 失败");
                return;
            }

            ApplyPreview(texture);
            SetStatus("图片加载完成");
            SetResultText(BuildResultSummary(result));
        }

        private void ApplyPreview(Texture2D texture)
        {
            ReleaseLoadedTexture();
            _loadedTexture = texture;

            if (DisplayImage == null)
                return;

            DisplayImage.texture = texture;
            if (_previewFitter != null && texture.height > 0)
                _previewFitter.aspectRatio = (float)texture.width / texture.height;
        }

        private void ClearPreview()
        {
            ReleaseLoadedTexture();

            if (DisplayImage != null)
                DisplayImage.texture = null;

            if (_previewFitter != null)
                _previewFitter.aspectRatio = 1f;

            SetStatus("预览已清空");
            SetResultText("预览已清空");
        }

        private void CleanupTempFiles()
        {
            ImagePickerHelper.CleanupTempFiles();
            SetStatus("临时缓存已清理");
        }

        private void ReleaseLoadedTexture()
        {
            if (_loadedTexture == null)
                return;

            Destroy(_loadedTexture);
            _loadedTexture = null;
        }

        private void RefreshRequestPreview()
        {
            SetRequestText(BuildRequest(_previewSource));
        }

        private void SetRequestText(ImagePickerRequest request)
        {
            if (RequestText == null || request == null)
                return;

            var builder = new StringBuilder();
            builder.AppendLine("请求预览:");
            builder.AppendLine($"Source: {request.Source}");
            builder.AppendLine($"Constraint: {GetConstraintLabel()}");
            builder.AppendLine($"Crop: {GetCropLabel()}");
            builder.AppendLine($"Compress: {GetCompressLabel()}");
            RequestText.text = builder.ToString();
        }

        private void SetResultText(string text)
        {
            if (ResultText != null)
                ResultText.text = $"结果信息:\n{text}";
        }

        private string BuildResultSummary(ImagePickerResult result)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Success");
            builder.AppendLine($"Path: {result.FilePath}");
            builder.AppendLine($"Size: {result.Width} x {result.Height}");
            builder.AppendLine($"File: {FormatBytes(result.FileSize)}");
            return builder.ToString();
        }

        private void SetStatus(string message, bool isError = false)
        {
            if (StatusText == null)
                return;

            StatusText.text = $"状态: {message}";
            StatusText.color = isError ? new Color(1f, 0.58f, 0.58f) : Color.white;
        }

        private T FindComponent<T>(string path) where T : Component
        {
            var target = GameObject.Find(path);
            return target != null ? target.GetComponent<T>() : null;
        }

        private string FormatBytes(long bytes)
        {
            if (bytes <= 0)
                return "0 B";

            if (bytes >= 1024 * 1024)
                return $"{bytes / 1024f / 1024f:0.##} MB";

            if (bytes >= 1024)
                return $"{bytes / 1024f:0.##} KB";

            return $"{bytes} B";
        }
    }
}
