# ImagePicker — 拍照/选图功能

跨平台图片选择工具，支持 Android / iOS / Editor，提供拍照、图库选择、裁剪、压缩等完整功能。

## 架构概览

```
ToolKit/Tools/ImagePicker/              ← 纯 C# 接口层 (无 Unity 依赖)
├── IImagePicker.cs                     ← 平台接口
├── ImagePickerRequest.cs               ← 请求参数
├── ImagePickerResult.cs                ← 返回结果 (含 ErrorCode 错误码)
├── EImagePickerError.cs                ← 错误码枚举
├── ImageConstraint.cs                  ← 图片约束 (大小/尺寸限制)
├── CropConfig.cs                       ← 裁剪配置
├── CompressConfig.cs                   ← 压缩配置
├── EImageSource.cs                     ← 来源枚举 (相册/拍照)
└── ECropShape.cs                       ← 裁剪形状枚举 (矩形/圆形)

UnityToolKit/Plugins/ImagePicker/       ← Unity 平台实现层
├── ImagePickerFactory.cs               ← 平台工厂 (自动选择实现)
├── ImagePickerHelper.cs                ← 辅助工具 (加载 Texture2D/Sprite)
├── ImageCompressor.cs                  ← 统一压缩器 (跨平台一致)
├── AndroidImagePicker.cs               ← Android 实现
├── IOSImagePicker.cs                   ← iOS 实现
├── EditorImagePicker.cs                ← Editor 模拟实现
├── Android/
│   ├── ImagePickerBridge.java          ← Java 桥接
│   ├── ImagePickerActivity.java        ← 选图/拍照 Activity
│   ├── AndroidManifest.xml
│   └── res/xml/toolkit_imagepicker_paths.xml
└── IOS/
    ├── ImagePickerBridge.h             ← ObjC 头文件
    └── ImagePickerBridge.m             ← ObjC 实现
```

## 快速使用

### 1. 最简单的选图

```csharp
using ToolKit.Tools.ImagePicker;
using UnityToolKit.Plugins.ImagePicker;

// 一行代码选图并加载为 Texture2D
ImagePickerHelper.PickAndLoadTexture(new ImagePickerRequest(), (texture, errorCode) =>
{
    if (texture != null)
        rawImage.texture = texture;
    else
        Debug.LogError($"选图失败: {errorCode}");
});
```

### 2. 拍照 + 裁剪为正方形 + 压缩

```csharp
var request = new ImagePickerRequest
{
    Source = EImageSource.Camera,
    Crop = new CropConfig
    {
        EnableCrop = true,
        Shape = ECropShape.Rectangle,
        AspectRatioX = 1,
        AspectRatioY = 1,
        MaxOutputWidth = 512,
        MaxOutputHeight = 512
    },
    Compress = new CompressConfig
    {
        EnableCompress = true,
        MaxWidth = 512,
        MaxHeight = 512,
        Quality = 80,
        MaxFileSize = 200 * 1024  // 最大 200KB
    }
};

ImagePickerHelper.PickAndLoadTexture(request, (texture, errorCode) =>
{
    if (texture != null)
        avatarImage.texture = texture;
});
```

### 3. 选图带约束限制 + 错误码处理

```csharp
var request = new ImagePickerRequest
{
    Source = EImageSource.Gallery,
    Constraint = new ImageConstraint
    {
        MinWidth = 200,
        MinHeight = 200,
        MaxFileSize = 10 * 1024 * 1024  // 最大 10MB
    }
};

var picker = ImagePickerFactory.Get();
picker.PickImage(request, result =>
{
    if (result.Success)
    {
        Debug.Log($"图片路径: {result.FilePath}");
        Debug.Log($"尺寸: {result.Width}x{result.Height}");
        Debug.Log($"大小: {result.FileSize} bytes");
    }
    else
    {
        // 业务层根据错误码处理
        switch (result.ErrorCode)
        {
            case EImagePickerError.Cancelled:
                Debug.Log("用户取消");
                break;
            case EImagePickerError.CameraPermissionDenied:
                ShowPermissionDialog("需要相机权限");
                break;
            case EImagePickerError.StoragePermissionDenied:
                ShowPermissionDialog("需要存储权限");
                break;
            case EImagePickerError.ConstraintViolation:
                ShowToast("图片不符合要求");
                break;
            default:
                Debug.LogError($"选图失败: {result.ErrorCode}, 详情: {result.ErrorDetail}");
                break;
        }
    }
});
```

### 4. 圆形头像裁剪

```csharp
var request = new ImagePickerRequest
{
    Source = EImageSource.Gallery,
    Crop = new CropConfig
    {
        EnableCrop = true,
        Shape = ECropShape.Circle,
        AspectRatioX = 1,
        AspectRatioY = 1
    },
    Compress = new CompressConfig
    {
        EnableCompress = true,
        MaxWidth = 256,
        MaxHeight = 256,
        Quality = 85
    }
};

ImagePickerHelper.PickAndLoadSprite(request, (sprite, errorCode) =>
{
    if (sprite != null)
        avatarImage.sprite = sprite;
});
```

## 错误码

失败时通过 `result.ErrorCode` (`EImagePickerError` 枚举) 返回给业务层，业务层根据错误码自行处理显示和重试策略。
`result.ErrorDetail` 为可选的调试信息，不建议直接展示给用户。

| 错误码 | 值 | 说明 |
|---|---|---|
| `None` | 0 | 无错误 |
| `Cancelled` | 1 | 用户取消 |
| `InvalidRequest` | 2 | 请求参数无效 |
| `CameraPermissionDenied` | 10 | 相机权限被拒绝 |
| `StoragePermissionDenied` | 11 | 存储权限被拒绝 |
| `CameraNotSupported` | 20 | 设备不支持拍照 |
| `PlatformNotSupported` | 21 | 平台不支持此操作 |
| `ImageDecodeFailed` | 30 | 图片解码失败 |
| `ImageNotFound` | 31 | 图片文件不存在 |
| `UnsupportedFormat` | 32 | 不支持的图片格式 |
| `ProcessFailed` | 33 | 图片处理异常 |
| `ConstraintViolation` | 40 | 不满足约束条件 |
| `CropFailed` | 50 | 裁剪失败 |
| `CompressFailed` | 60 | 压缩失败 |
| `NotInitialized` | 90 | 未初始化 |
| `ConfigParseFailed` | 91 | 配置解析失败 |
| `PlatformError` | 99 | 平台相关错误 |

## 平台配置

### Android

1. **uCrop 裁剪库 (可选)**：如需裁剪功能，需将 [uCrop](https://github.com/Yalantis/uCrop) 的 AAR 文件放入 `Plugins/Android/` 目录。不引入时裁剪步骤自动跳过。
2. **权限**：AndroidManifest.xml 已声明相机和存储权限，运行时会自动请求。
3. **FileProvider**：已配置用于 Android 7.0+ 的拍照文件分享。

### iOS

1. **TOCropViewController (可选)**：如需裁剪功能，通过 CocoaPods 添加 `pod 'TOCropViewController'`。不引入时裁剪步骤自动跳过。
2. **Info.plist 权限描述** (必须手动添加):
   - `NSCameraUsageDescription` — 拍照功能需要
   - `NSPhotoLibraryUsageDescription` — iOS 13 及以下图库访问需要 (iOS 14+ 使用 PHPicker 无需此权限)

### Editor

在编辑器中使用文件选择对话框模拟图库选择，支持基本的约束校验和压缩，不支持拍照和裁剪。

## API 参考

| 类型 | 说明 |
|---|---|
| `IImagePicker` | 平台接口，定义 `PickImage()` 方法 |
| `ImagePickerFactory` | 工厂类，`Get()` 获取单例，`Create()` 创建新实例 |
| `ImagePickerHelper` | 辅助工具，提供 `PickAndLoadTexture/Sprite` 等便捷方法 |
| `ImagePickerRequest` | 请求参数，包含 Source、Constraint、Crop、Compress |
| `ImagePickerResult` | 返回结果，包含 Success、ErrorCode、ErrorDetail、FilePath、Width、Height、FileSize |
| `EImagePickerError` | 错误码枚举，业务层据此处理错误显示和重试策略 |
| `ImageConstraint` | 约束条件：MaxFileSize、Min/MaxWidth、Min/MaxHeight |
| `CropConfig` | 裁剪配置：Shape、AspectRatio、MaxOutput |
| `CompressConfig` | 压缩配置：MaxWidth/Height、Quality、MaxFileSize |
| `ImageCompressor` | 统一压缩器，在 Unity C# 侧执行，保证跨平台压缩结果一致 |

## 处理流程

```
选图/拍照 → EXIF方向修正 → 约束校验 → 裁剪 (可选) → 压缩 (可选, C#侧统一) → 返回结果
```

- 压缩统一在 Unity C# 侧由 `ImageCompressor` 执行，保证 Android/iOS/Editor 三端压缩结果一致
- Native 层仅以高质量保存原始图片，并修正 EXIF 旋转方向
- 每一步失败都通过回调返回错误码 (`EImagePickerError`)，不会抛出异常
