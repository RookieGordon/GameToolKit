/*
 * datetime     : 2026/2/24
 * description  : 跨平台统一图片压缩器
 *                在 Unity C# 侧统一处理图片压缩 (尺寸缩放 + 质量压缩)
 *                保证 Android / iOS / Editor 压缩结果一致
 *
 *                异步模式 (CompressAsync):
 *                  - 文件读写在 ThreadPool 子线程执行
 *                  - 缩放使用 CPU 双线性插值在子线程执行
 *                  - 编码分帧执行 (每帧一次 EncodeToJPG), 不阻塞主线程
 *                  - 返回 CompressRequest (CustomYieldInstruction), 可直接 yield return
 */

using System;
using System.IO;
using System.Threading;
using ToolKit.Tools.ImagePicker;
using UnityEngine;

namespace UnityToolKit.Plugins.ImagePicker
{
    /// <summary>
    /// 跨平台统一图片压缩器
    /// <para>在 Unity 侧统一执行压缩, 保证不同平台的压缩结果一致</para>
    /// <para>仅提供异步模式 (<see cref="CompressAsync"/>), 避免大图压缩阻塞主线程</para>
    /// </summary>
    public static class ImageCompressor
    {
        // ---- 质量搜索常量 ----
        private const int QUALITY_MAX = 95;
        private const int QUALITY_MIN = 10;
        private const int QUALITY_SEARCH_PRECISION = 3;    // 二分搜索精度, quality 差值 <= 此值时停止
        private const float RESOLUTION_SHRINK_FACTOR = 0.75f; // 每次降分辨率的缩放因子
        private const int MIN_DIMENSION = 64;               // 分辨率下限

        #region Async API

        /// <summary>
        /// 异步压缩选图结果, 返回 <see cref="CompressRequest"/> (可直接 yield return)
        /// <para>文件 I/O 和缩放在子线程执行, 编码分帧执行, 不阻塞主线程</para>
        /// </summary>
        /// <param name="result">原始选图结果</param>
        /// <param name="config">压缩配置</param>
        /// <returns>压缩请求, 通过 <see cref="CompressRequest.Result"/> 获取结果</returns>
        public static CompressRequest CompressAsync(ImagePickerResult result, CompressConfig config)
        {
            return new CompressRequest(result, config);
        }

        #endregion

        #region Internal - Skip Detection

        /// <summary>
        /// 快速检测是否可以跳过压缩: 文件大小和图片尺寸均满足限制
        /// <para>通过读取文件头判断尺寸, 避免完整解码</para>
        /// </summary>
        private static bool CanSkipCompress(byte[] imageData, CompressConfig config)
        {
            // 检查文件大小
            if (config.MaxFileSize > 0 && imageData.Length > config.MaxFileSize)
                return false;

            // 通过文件头快速读取尺寸
            ReadImageDimensions(imageData, out int width, out int height);
            if (width <= 0 || height <= 0)
                return false; // 无法读取尺寸, 不跳过, 走正常流程

            if (config.MaxWidth > 0 && width > config.MaxWidth)
                return false;
            if (config.MaxHeight > 0 && height > config.MaxHeight)
                return false;

            return true;
        }

        /// <summary>
        /// 从图片文件头快速读取宽高, 支持 PNG / JPEG 格式
        /// </summary>
        private static void ReadImageDimensions(byte[] data, out int width, out int height)
        {
            width = -1;
            height = -1;
            if (data == null || data.Length < 24) return;

            // PNG: IHDR chunk 中第 16~19 字节是宽度, 20~23 是高度 (大端)
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
                height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
                return;
            }

            // JPEG: 遍历标记段找 SOF
            if (data[0] == 0xFF && data[1] == 0xD8)
            {
                ReadJpegDimensions(data, out width, out height);
            }
        }

        /// <summary>
        /// 遍历 JPEG 标记段查找 SOF 获取宽高
        /// </summary>
        private static void ReadJpegDimensions(byte[] data, out int width, out int height)
        {
            width = -1;
            height = -1;
            int offset = 2;

            while (offset + 4 < data.Length)
            {
                if (data[offset] != 0xFF) break;
                byte marker = data[offset + 1];

                // SOF0~SOF3, SOF5~SOF7, SOF9~SOF11, SOF13~SOF15
                if ((marker >= 0xC0 && marker <= 0xC3) ||
                    (marker >= 0xC5 && marker <= 0xC7) ||
                    (marker >= 0xC9 && marker <= 0xCB) ||
                    (marker >= 0xCD && marker <= 0xCF))
                {
                    if (offset + 9 < data.Length)
                    {
                        height = (data[offset + 5] << 8) | data[offset + 6];
                        width = (data[offset + 7] << 8) | data[offset + 8];
                    }
                    return;
                }

                if (offset + 3 >= data.Length) break;
                int segLen = (data[offset + 2] << 8) | data[offset + 3];
                if (segLen < 2) break; // 防止畸形数据导致无限循环
                offset += 2 + segLen;
            }
        }

        #endregion

        #region Internal - Scaling

        /// <summary>
        /// 计算等比缩放后的尺寸
        /// </summary>
        internal static void CalcScaledSize(ref int width, ref int height, int maxWidth, int maxHeight)
        {
            if (maxWidth > 0 && width > maxWidth)
            {
                float ratio = (float)maxWidth / width;
                width = maxWidth;
                height = Mathf.RoundToInt(height * ratio);
            }
            if (maxHeight > 0 && height > maxHeight)
            {
                float ratio = (float)maxHeight / height;
                height = maxHeight;
                width = Mathf.RoundToInt(width * ratio);
            }

            width = Mathf.Max(width, 1);
            height = Mathf.Max(height, 1);
        }

        /// <summary>
        /// CPU 双线性插值缩放 (可在子线程执行)
        /// <para>预计算 x 轴映射表优化, 减少逐像素重复计算</para>
        /// </summary>
        internal static Color32[] BilinearScale(Color32[] source, int srcW, int srcH, int dstW, int dstH)
        {
            var dest = new Color32[dstW * dstH];
            float xRatio = (float)(srcW - 1) / Mathf.Max(1, dstW);
            float yRatio = (float)(srcH - 1) / Mathf.Max(1, dstH);

            // 预计算 x 轴映射, 避免每行重复计算
            var xFloors = new int[dstW];
            var xCeils = new int[dstW];
            var xLerps = new float[dstW];
            for (int x = 0; x < dstW; x++)
            {
                float srcX = x * xRatio;
                int xf = (int)srcX;
                xFloors[x] = xf;
                xCeils[x] = Mathf.Min(xf + 1, srcW - 1);
                xLerps[x] = srcX - xf;
            }

            for (int y = 0; y < dstH; y++)
            {
                float srcY = y * yRatio;
                int yFloor = (int)srcY;
                int yCeil = Mathf.Min(yFloor + 1, srcH - 1);
                float yLerp = srcY - yFloor;
                float invYLerp = 1f - yLerp;

                int rowFloor = yFloor * srcW;
                int rowCeil = yCeil * srcW;
                int dstRow = y * dstW;

                for (int x = 0; x < dstW; x++)
                {
                    int xf = xFloors[x];
                    int xc = xCeils[x];
                    float xLerp = xLerps[x];
                    float invXLerp = 1f - xLerp;

                    Color32 c00 = source[rowFloor + xf];
                    Color32 c10 = source[rowFloor + xc];
                    Color32 c01 = source[rowCeil + xf];
                    Color32 c11 = source[rowCeil + xc];

                    dest[dstRow + x] = new Color32(
                        (byte)(invYLerp * (invXLerp * c00.r + xLerp * c10.r) + yLerp * (invXLerp * c01.r + xLerp * c11.r)),
                        (byte)(invYLerp * (invXLerp * c00.g + xLerp * c10.g) + yLerp * (invXLerp * c01.g + xLerp * c11.g)),
                        (byte)(invYLerp * (invXLerp * c00.b + xLerp * c10.b) + yLerp * (invXLerp * c01.b + xLerp * c11.b)),
                        255);
                }
            }

            return dest;
        }

        #endregion

        #region CompressRequest (Async)

        /// <summary>
        /// 异步压缩请求, 继承 CustomYieldInstruction, 可直接 yield return 使用
        /// <para>状态机驱动: 文件读取(子线程) → 跳过检测 → 解码 → 缩放(子线程) → 二分搜索编码(分帧) → 写文件(子线程)</para>
        /// </summary>
        public class CompressRequest : CustomYieldInstruction
        {
            /// <summary> 压缩结果 (完成后有值) </summary>
            public ImagePickerResult Result { get; private set; }

            /// <summary> 是否已完成 </summary>
            public bool IsDone { get; private set; }

            private readonly CompressConfig _config;
            private readonly string _srcFilePath;

            // 状态机阶段
            private enum Phase
            {
                ReadFile,       // 子线程读取文件
                CheckSkip,      // 检查是否可跳过
                Decode,         // 主线程解码纹理
                CpuScale,       // 子线程缩放
                CreateEncodeTex,// 创建编码纹理 & 初始化二分搜索
                TryEncode,      // 二分搜索编码 (每帧一次)
                ReduceResolution,// 降低分辨率
                ReScale,        // 子线程重新缩放
                WriteFile,      // 子线程写文件
            }

            private Phase _phase;

            // 子线程同步
            private volatile bool _threadDone;
            private volatile Exception _threadException;

            // 数据
            private byte[] _imageData;
            private Color32[] _sourcePixels;
            private int _originalWidth, _originalHeight;
            private int _targetWidth, _targetHeight;
            private Color32[] _scaledPixels;
            private Texture2D _encodeTex;
            private byte[] _jpgData;
            private string _outputPath;

            // 二分搜索状态
            private int _qualityLow, _qualityHigh, _currentQuality;
            private int _bestQuality;
            private byte[] _bestJpgData;

            public CompressRequest(ImagePickerResult srcResult, CompressConfig config)
            {
                _config = config;

                if (srcResult == null || !srcResult.Success)
                {
                    Finish(srcResult);
                    return;
                }

                if (config == null || !config.EnableCompress)
                {
                    Finish(srcResult);
                    return;
                }

                _srcFilePath = srcResult.FilePath;
                if (string.IsNullOrEmpty(_srcFilePath) || !File.Exists(_srcFilePath))
                {
                    Finish(ImagePickerResult.Fail(EImagePickerError.ImageNotFound));
                    return;
                }

                StartReadFile();
            }

            public override bool keepWaiting
            {
                get
                {
                    if (IsDone) return false;
                    Advance();
                    return !IsDone;
                }
            }

            private void Advance()
            {
                switch (_phase)
                {
                    case Phase.ReadFile:
                        if (!_threadDone) return;
                        if (_threadException != null || _imageData == null)
                        {
                            Fail(EImagePickerError.ImageNotFound,
                                $"读取图片失败: {_threadException?.Message}");
                            return;
                        }
                        _phase = Phase.CheckSkip;
                        return;

                    case Phase.CheckSkip:
                        if (CanSkipCompress(_imageData, _config))
                        {
                            var info = new FileInfo(_srcFilePath);
                            ReadImageDimensions(_imageData, out int w, out int h);
                            _imageData = null;
                            Finish(ImagePickerResult.Succeed(_srcFilePath,
                                w > 0 ? w : 0, h > 0 ? h : 0, info.Length));
                            return;
                        }
                        _phase = Phase.Decode;
                        return;

                    case Phase.Decode:
                        if (!DecodeAndExtractPixels()) return;
                        StartCpuScale();
                        _phase = Phase.CpuScale;
                        return;

                    case Phase.CpuScale:
                        if (!_threadDone) return;
                        if (_threadException != null)
                        {
                            Fail(EImagePickerError.CompressFailed,
                                $"缩放失败: {_threadException.Message}");
                            return;
                        }
                        _phase = Phase.CreateEncodeTex;
                        return;

                    case Phase.CreateEncodeTex:
                        if (!CreateEncodeTex()) return;
                        _phase = Phase.TryEncode;
                        return;

                    case Phase.TryEncode:
                        HandleTryEncode();
                        return;

                    case Phase.ReduceResolution:
                        HandleReduceResolution();
                        return;

                    case Phase.ReScale:
                        if (!_threadDone) return;
                        if (_threadException != null)
                        {
                            Fail(EImagePickerError.CompressFailed,
                                $"重新缩放失败: {_threadException.Message}");
                            return;
                        }
                        _phase = Phase.CreateEncodeTex;
                        return;

                    case Phase.WriteFile:
                        if (!_threadDone) return;
                        if (_threadException != null)
                        {
                            Fail(EImagePickerError.CompressFailed,
                                $"写入文件失败: {_threadException.Message}");
                            return;
                        }
                        _jpgData = null;
                        Finish(ImagePickerResult.Succeed(
                            _outputPath, _targetWidth, _targetHeight,
                            new FileInfo(_outputPath).Length));
                        return;
                }
            }

            #region Phases

            private void StartReadFile()
            {
                _threadDone = false;
                _threadException = null;
                string path = _srcFilePath;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (!File.Exists(path))
                            throw new FileNotFoundException($"文件不存在: {path}");

                        var fi = new FileInfo(path);
                        if (fi.Length == 0)
                            throw new InvalidDataException($"文件为空: {path}");

                        _imageData = File.ReadAllBytes(path);
                    }
                    catch (Exception e) { _threadException = e; }
                    finally { _threadDone = true; }
                });
                _phase = Phase.ReadFile;
            }

            private bool DecodeAndExtractPixels()
            {
                if (_imageData == null || _imageData.Length == 0)
                {
                    Fail(EImagePickerError.ImageDecodeFailed, "图片数据为空");
                    return false;
                }

                var tex = new Texture2D(2, 2);
                if (!tex.LoadImage(_imageData))
                {
                    UnityEngine.Object.Destroy(tex);
                    Fail(EImagePickerError.ImageDecodeFailed, "无法解码图片");
                    return false;
                }

                if (tex.width <= 1 || tex.height <= 1)
                {
                    UnityEngine.Object.Destroy(tex);
                    Fail(EImagePickerError.ImageDecodeFailed,
                        $"纹理尺寸无效: {tex.width}x{tex.height}");
                    return false;
                }

                _imageData = null; // 尽早释放
                _originalWidth = tex.width;
                _originalHeight = tex.height;

                try
                {
                    _sourcePixels = tex.GetPixels32();
                }
                catch (Exception e)
                {
                    UnityEngine.Object.Destroy(tex);
                    Fail(EImagePickerError.CompressFailed, $"GetPixels32 失败: {e.Message}");
                    return false;
                }

                UnityEngine.Object.Destroy(tex);

                // 计算初始目标尺寸
                _targetWidth = _originalWidth;
                _targetHeight = _originalHeight;
                CalcScaledSize(ref _targetWidth, ref _targetHeight,
                    _config.MaxWidth, _config.MaxHeight);

                return true;
            }

            private void StartCpuScale()
            {
                Color32[] src = _sourcePixels;
                int srcW = _originalWidth, srcH = _originalHeight;
                int dstW = _targetWidth, dstH = _targetHeight;

                _threadDone = false;
                _threadException = null;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        _scaledPixels = (srcW == dstW && srcH == dstH)
                            ? src
                            : BilinearScale(src, srcW, srcH, dstW, dstH);
                    }
                    catch (Exception e) { _threadException = e; }
                    finally { _threadDone = true; }
                });
            }

            private bool CreateEncodeTex()
            {
                if (_scaledPixels == null || _scaledPixels.Length != _targetWidth * _targetHeight)
                {
                    Fail(EImagePickerError.CompressFailed,
                        $"像素数据异常: {_scaledPixels?.Length ?? 0} vs {_targetWidth * _targetHeight}");
                    return false;
                }

                try
                {
                    _encodeTex = new Texture2D(_targetWidth, _targetHeight, TextureFormat.RGB24, false);
                    _encodeTex.SetPixels32(_scaledPixels);
                    _encodeTex.Apply(false, false);
                    _scaledPixels = null;
                }
                catch (Exception e)
                {
                    Fail(EImagePickerError.CompressFailed, $"创建纹理失败: {e.Message}");
                    return false;
                }

                // 初始化二分搜索
                int initialQuality = _config.Quality > 0 ? _config.Quality : 85;
                _qualityLow = QUALITY_MIN;
                _qualityHigh = Mathf.Min(initialQuality, QUALITY_MAX);
                _bestQuality = -1;
                _bestJpgData = null;
                _currentQuality = _config.MaxFileSize <= 0
                    ? _qualityHigh
                    : (_qualityLow + _qualityHigh) / 2;

                return true;
            }

            private void HandleTryEncode()
            {
                try
                {
                    byte[] encoded = _encodeTex.EncodeToJPG(_currentQuality);

                    if (_config.MaxFileSize <= 0)
                    {
                        _jpgData = encoded;
                        FinishEncoding();
                        return;
                    }

                    if (encoded.Length <= _config.MaxFileSize)
                    {
                        _bestQuality = _currentQuality;
                        _bestJpgData = encoded;
                        _qualityLow = _currentQuality + 1;
                    }
                    else
                    {
                        _qualityHigh = _currentQuality - 1;
                    }

                    if (_qualityLow > _qualityHigh || (_qualityHigh - _qualityLow) < QUALITY_SEARCH_PRECISION)
                    {
                        if (_bestJpgData != null)
                        {
                            _jpgData = _bestJpgData;
                            _bestJpgData = null;
                            FinishEncoding();
                        }
                        else
                        {
                            // 当前分辨率下最低质量仍超限 → 缩小分辨率
                            DestroyEncodeTex();
                            _phase = Phase.ReduceResolution;
                        }
                        return;
                    }

                    _currentQuality = (_qualityLow + _qualityHigh) / 2;
                    // 下一帧继续搜索
                }
                catch (Exception e)
                {
                    Fail(EImagePickerError.CompressFailed, $"编码失败: {e.Message}");
                }
            }

            private void HandleReduceResolution()
            {
                int newW = Mathf.Max(MIN_DIMENSION, Mathf.RoundToInt(_targetWidth * RESOLUTION_SHRINK_FACTOR));
                int newH = Mathf.Max(MIN_DIMENSION, Mathf.RoundToInt(_targetHeight * RESOLUTION_SHRINK_FACTOR));

                if (newW >= _targetWidth && newH >= _targetHeight)
                {
                    // 已达最小分辨率, 用最低质量输出
                    Debug.LogWarning($"[ImageCompressor] 已达最小分辨率 {_targetWidth}x{_targetHeight}, " +
                                     $"无法满足目标大小 {_config.MaxFileSize / 1024f:F1}KB, 使用最低质量");
                    try
                    {
                        var pixels = (_targetWidth == _originalWidth && _targetHeight == _originalHeight)
                            ? _sourcePixels
                            : BilinearScale(_sourcePixels, _originalWidth, _originalHeight,
                                _targetWidth, _targetHeight);
                        _encodeTex = new Texture2D(_targetWidth, _targetHeight, TextureFormat.RGB24, false);
                        _encodeTex.SetPixels32(pixels);
                        _encodeTex.Apply(false, false);
                        _jpgData = _encodeTex.EncodeToJPG(QUALITY_MIN);
                        DestroyEncodeTex();
                        FinishEncoding();
                    }
                    catch (Exception e)
                    {
                        Fail(EImagePickerError.CompressFailed, $"最终编码失败: {e.Message}");
                    }
                    return;
                }

                _targetWidth = newW;
                _targetHeight = newH;
                StartCpuScale();
                _phase = Phase.ReScale;
            }

            private void FinishEncoding()
            {
                DestroyEncodeTex();
                _sourcePixels = null;
                StartWriteFile();
                _phase = Phase.WriteFile;
            }

            private void StartWriteFile()
            {
                string dir = Path.Combine(Application.temporaryCachePath, "ImagePicker");
                _outputPath = Path.Combine(dir,
                    $"compressed_{DateTime.Now:yyyyMMdd_HHmmss}_{UnityEngine.Random.Range(0, 9999)}.jpg");

                string outputPath = _outputPath;
                byte[] jpgData = _jpgData;

                _threadDone = false;
                _threadException = null;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        string d = Path.GetDirectoryName(outputPath);
                        if (!string.IsNullOrEmpty(d) && !Directory.Exists(d))
                            Directory.CreateDirectory(d);
                        File.WriteAllBytes(outputPath, jpgData);
                    }
                    catch (Exception e) { _threadException = e; }
                    finally { _threadDone = true; }
                });
            }

            #endregion

            #region Helpers

            private void DestroyEncodeTex()
            {
                if (_encodeTex != null)
                {
                    UnityEngine.Object.Destroy(_encodeTex);
                    _encodeTex = null;
                }
            }

            private void Finish(ImagePickerResult result)
            {
                Result = result;
                IsDone = true;
            }

            private void Fail(EImagePickerError code, string detail)
            {
                DestroyEncodeTex();
                _sourcePixels = null;
                _scaledPixels = null;
                _imageData = null;
                _jpgData = null;
                _bestJpgData = null;
                Finish(ImagePickerResult.Fail(code, detail));
            }

            #endregion
        }

        #endregion
    }
}
