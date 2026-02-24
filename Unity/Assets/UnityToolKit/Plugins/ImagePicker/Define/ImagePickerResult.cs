/*
 * datetime     : 2026/2/24
 * description  : 图片选择结果
 *                失败时通过 ErrorCode 传递错误码给业务层
 *                ErrorDetail 为可选的调试信息, 不建议直接展示给用户
 */

namespace ToolKit.Tools.ImagePicker
{
    /// <summary>
    /// 图片选择结果
    /// </summary>
    public class ImagePickerResult
    {
        /// <summary> 是否成功 </summary>
        public bool Success { get; set; }

        /// <summary> 错误码 (失败时有值, 业务层据此处理) </summary>
        public EImagePickerError ErrorCode { get; set; }

        /// <summary> 错误详情 (可选, 用于调试日志, 不建议直接展示给用户) </summary>
        public string ErrorDetail { get; set; }

        /// <summary> 结果图片文件路径 (成功时有值) </summary>
        public string FilePath { get; set; }

        /// <summary> 图片宽度 (像素) </summary>
        public int Width { get; set; }

        /// <summary> 图片高度 (像素) </summary>
        public int Height { get; set; }

        /// <summary> 文件大小 (字节) </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// 创建成功结果
        /// </summary>
        public static ImagePickerResult Succeed(string filePath, int width, int height, long fileSize)
        {
            return new ImagePickerResult
            {
                Success = true,
                ErrorCode = EImagePickerError.None,
                FilePath = filePath,
                Width = width,
                Height = height,
                FileSize = fileSize
            };
        }

        /// <summary>
        /// 创建失败结果
        /// </summary>
        /// <param name="code">错误码</param>
        /// <param name="detail">可选的错误详情 (调试用)</param>
        public static ImagePickerResult Fail(EImagePickerError code, string detail = null)
        {
            return new ImagePickerResult
            {
                Success = false,
                ErrorCode = code,
                ErrorDetail = detail
            };
        }
    }
}
