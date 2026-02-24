/*
 * datetime     : 2026/2/25
 * description  : 图片选择器错误码
 *                业务层根据错误码自行处理显示和逻辑
 *                错误码值 (int) 与 Native 层保持一致
 */

namespace ToolKit.Tools.ImagePicker
{
    /// <summary>
    /// 图片选择器错误码
    /// <para>业务层可根据错误码决定用户提示或重试策略</para>
    /// <para>错误码值与 Android/iOS Native 层保持一致</para>
    /// </summary>
    public enum EImagePickerError
    {
        /// <summary> 无错误 </summary>
        None = 0,

        // -------- 通用 (1~9) --------

        /// <summary> 用户取消选图 </summary>
        Cancelled = 1,

        /// <summary> 请求参数无效 (request 为 null 等) </summary>
        InvalidRequest = 2,

        // -------- 权限 (10~19) --------

        /// <summary> 相机权限被拒绝 </summary>
        CameraPermissionDenied = 10,

        /// <summary> 存储权限被拒绝 </summary>
        StoragePermissionDenied = 11,

        // -------- 设备能力 (20~29) --------

        /// <summary> 设备不支持拍照 (模拟器、无相机设备等) </summary>
        CameraNotSupported = 20,

        /// <summary> 当前平台不支持此操作 </summary>
        PlatformNotSupported = 21,

        // -------- 图片处理 (30~39) --------

        /// <summary> 图片解码失败 (格式损坏或不支持) </summary>
        ImageDecodeFailed = 30,

        /// <summary> 图片文件不存在 </summary>
        ImageNotFound = 31,

        /// <summary> 不支持的图片格式 </summary>
        UnsupportedFormat = 32,

        /// <summary> 图片处理过程中异常 </summary>
        ProcessFailed = 33,

        // -------- 约束校验 (40~49) --------

        /// <summary> 图片不满足约束条件 (尺寸/文件大小等) </summary>
        ConstraintViolation = 40,

        // -------- 裁剪 (50~59) --------

        /// <summary> 裁剪失败 </summary>
        CropFailed = 50,

        // -------- 压缩 (60~69) --------

        /// <summary> 压缩失败 </summary>
        CompressFailed = 60,

        // -------- 初始化/平台 (90~99) --------

        /// <summary> 未初始化 </summary>
        NotInitialized = 90,

        /// <summary> 配置解析失败 </summary>
        ConfigParseFailed = 91,

        /// <summary> 平台相关错误 (未归类的原生错误) </summary>
        PlatformError = 99,
    }
}
