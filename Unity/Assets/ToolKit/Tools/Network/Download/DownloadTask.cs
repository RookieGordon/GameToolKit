/*
 * datetime     : 2026/2/20
 * description  : 下载任务核心实现
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;

namespace ToolKit.Tools.Network
{
    /// <summary>
    /// 下载任务
    /// <para>支持断点续传、暂停/取消、重试、进度与速度回调</para>
    /// </summary>
    public class DownloadTask : IDownloadTask, IDisposable
    {
        #region Fields

        private readonly string _url;
        private readonly string _savePath;
        private readonly int _maxRetries;
        private readonly int _retryDelayMs;
        private readonly int _bufferSize;
        private readonly int _connectTimeoutMs;
        private readonly int _readTimeoutMs;
        private readonly string _tag;

        private volatile EDownloadStatus _status;
        private DownloadProgress _progress;
        private readonly object _progressLock = new object();

        private int _retryCount;
        private long _downloadedBytes;
        private bool _serverSupportsResume;

        private volatile bool _isPauseRequested;
        private CancellationTokenSource _cts;
        private HttpWebRequest _currentRequest;
        private bool _disposed;

        #endregion

        #region Callbacks

        /// <summary> 下载开始回调 </summary>
        public Action<IDownloadTask> OnStart { get; set; }

        /// <summary> 下载进度回调 </summary>
        public Action<IDownloadTask, DownloadProgress> OnProgress { get; set; }

        /// <summary> 下载完成回调 </summary>
        public Action<IDownloadTask> OnCompleted { get; set; }

        /// <summary>
        /// 下载失败回调
        /// <para>参数: (任务, 错误类型), 具体异常信息已输出到日志</para>
        /// </summary>
        public Action<IDownloadTask, EDownloadError> OnFailed { get; set; }

        /// <summary> 下载取消回调 </summary>
        public Action<IDownloadTask> OnCancelled { get; set; }

        #endregion

        #region Properties

        /// <summary>
        /// 任务标签, 用于业务层定位具体任务
        /// <para>未指定时默认为下载文件名</para>
        /// </summary>
        public string Tag => _tag;

        /// <summary> 下载地址 </summary>
        public string Url => _url;

        /// <summary> 保存路径 </summary>
        public string SavePath => _savePath;

        /// <summary> 当前状态 </summary>
        public EDownloadStatus Status => _status;

        /// <summary> 当前下载进度 </summary>
        public DownloadProgress Progress
        {
            get { lock (_progressLock) return _progress; }
        }

        /// <summary> 当前重试次数 </summary>
        public int RetryCount => _retryCount;

        /// <summary> 最大重试次数 </summary>
        public int MaxRetries => _maxRetries;

        #endregion

        #region Constructor

        /// <summary>
        /// 创建下载任务
        /// </summary>
        /// <param name="url">下载地址</param>
        /// <param name="savePath">文件保存路径</param>
        /// <param name="maxRetries">最大重试次数</param>
        /// <param name="retryDelayMs">重试间隔 (毫秒)</param>
        /// <param name="bufferSize">读写缓冲区大小</param>
        /// <param name="connectTimeoutMs">连接超时时间 (毫秒), 即建立 TCP 连接和收到响应头的最大等待时间</param>
        /// <param name="readTimeoutMs">读取超时时间 (毫秒), 即下载过程中单次读取操作的最大等待时间</param>
        /// <param name="tag">任务标签, 为 null 时自动使用文件名</param>
        public DownloadTask(string url, string savePath,
            int maxRetries = 3, int retryDelayMs = 1000,
            int bufferSize = 8192,
            int connectTimeoutMs = 30000, int readTimeoutMs = 30000,
            string tag = null)
        {
            _url = url ?? throw new ArgumentNullException(nameof(url));
            _savePath = savePath ?? throw new ArgumentNullException(nameof(savePath));
            _tag = tag ?? Path.GetFileName(savePath);
            _maxRetries = Math.Max(0, maxRetries);
            _retryDelayMs = Math.Max(0, retryDelayMs);
            _bufferSize = Math.Max(1024, bufferSize);
            _connectTimeoutMs = Math.Max(1000, connectTimeoutMs);
            _readTimeoutMs = Math.Max(1000, readTimeoutMs);
            _status = EDownloadStatus.Pending;
            _progress = new DownloadProgress { TotalBytes = -1 };
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 暂停下载
        /// </summary>
        public void Pause()
        {
            if (_status != EDownloadStatus.Downloading) return;
            _isPauseRequested = true;
            Abort();
        }

        /// <summary>
        /// 取消下载
        /// </summary>
        public void Cancel()
        {
            if (_status == EDownloadStatus.Completed || _status == EDownloadStatus.Cancelled) return;
            Abort();
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// 执行下载任务 (由下载器调用)
        /// <para>支持暂停后重新调用以断点续传</para>
        /// </summary>
        internal async Task ExecuteAsync(CancellationToken externalToken = default)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, externalToken))
            {
                var token = linkedCts.Token;

                // 校验已下载的文件状态
                ValidateResumeState();

                _status = EDownloadStatus.Downloading;
                OnStart?.Invoke(this);

                for (int attempt = 0; attempt <= _maxRetries; attempt++)
                {
                    try
                    {
                        _retryCount = attempt;
                        await DownloadCoreAsync(token);

                        // 下载结束瞬间可能收到暂停请求
                        if (_isPauseRequested)
                        {
                            _isPauseRequested = false;
                            _status = EDownloadStatus.Paused;
                            return;
                        }

                        _status = EDownloadStatus.Completed;
                        OnCompleted?.Invoke(this);
                        return;
                    }
                    catch (Exception ex)
                    {
                        // 暂停请求
                        if (_isPauseRequested)
                        {
                            _isPauseRequested = false;
                            _status = EDownloadStatus.Paused;
                            return;
                        }

                        // 取消请求
                        if (_cts.IsCancellationRequested || externalToken.IsCancellationRequested)
                        {
                            _status = EDownloadStatus.Cancelled;
                            OnCancelled?.Invoke(this);
                            return;
                        }

                        // 还有重试机会
                        if (attempt < _maxRetries)
                        {
                            if (!await TryDelayAsync(_retryDelayMs, token))
                                return;
                            continue;
                        }

                        // 最终失败: 分类异常并记录日志
                        _status = EDownloadStatus.Failed;
                        var errorType = ClassifyException(ex);
                        Log.Error($"[DownloadTask] 下载失败 [{_tag}]: {errorType} - {ex}");
                        OnFailed?.Invoke(this, errorType);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// 重置任务到初始状态
        /// </summary>
        internal void Reset()
        {
            _status = EDownloadStatus.Pending;
            lock (_progressLock)
            {
                _progress = new DownloadProgress { TotalBytes = -1 };
            }
            _downloadedBytes = 0;
            _retryCount = 0;
            _isPauseRequested = false;
            _serverSupportsResume = false;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// HTTP 下载核心逻辑
        /// </summary>
        private async Task DownloadCoreAsync(CancellationToken token)
        {
            var request = (HttpWebRequest)WebRequest.Create(_url);
            _currentRequest = request;
            request.Method = "GET";
            request.Timeout = _connectTimeoutMs;
            request.ReadWriteTimeout = _readTimeoutMs;

            // 断点续传: 设置 Range 请求头
            if (_downloadedBytes > 0 && _serverSupportsResume)
            {
                request.AddRange(_downloadedBytes);
            }

            // 注册取消回调: Token 触发时中止请求
            await using (token.Register(() => { try { request.Abort(); }catch { /* ignored */ } }))
            {
                HttpWebResponse response = null;
                Stream responseStream = null;
                FileStream fileStream = null;

                try
                {
                    response = (HttpWebResponse)await request.GetResponseAsync();

                    // 首次请求检测服务器是否支持断点续传
                    if (_downloadedBytes == 0)
                    {
                        var acceptRanges = response.Headers["Accept-Ranges"];
                        _serverSupportsResume = !string.IsNullOrEmpty(acceptRanges)
                            && acceptRanges.IndexOf("bytes", StringComparison.OrdinalIgnoreCase) >= 0;
                    }

                    // 计算总大小
                    UpdateTotalBytes(response);

                    responseStream = response.GetResponseStream();
                    if (responseStream == null)
                        throw new IOException("响应流为空");

                    // 确保保存目录存在
                    var directory = Path.GetDirectoryName(_savePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // 确定文件写入模式
                    var fileMode = (_downloadedBytes > 0
                        && response.StatusCode == HttpStatusCode.PartialContent)
                        ? FileMode.Append
                        : FileMode.Create;

                    // 服务器未返回 206, 需要从头开始
                    if (fileMode == FileMode.Create)
                    {
                        _downloadedBytes = 0;
                        UpdateProgress(0, 0);
                    }

                    fileStream = new FileStream(_savePath, fileMode, FileAccess.Write, FileShare.None);
                    await ReadAndWriteAsync(responseStream, fileStream, token);
                }
                finally
                {
                    fileStream?.Dispose();
                    responseStream?.Dispose();
                    response?.Dispose();
                    _currentRequest = null;
                }
            }
        }

        /// <summary>
        /// 从响应流读取数据并写入文件, 同时更新进度和速度
        /// </summary>
        private async Task ReadAndWriteAsync(Stream responseStream, FileStream fileStream, CancellationToken token)
        {
            var buffer = new byte[_bufferSize];
            var stopwatch = Stopwatch.StartNew();
            var speedBytes = 0L;
            var lastSpeedUpdateMs = stopwatch.ElapsedMilliseconds;

            while (true)
            {
                token.ThrowIfCancellationRequested();

                int bytesRead;
                try
                {
                    bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, token);
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }

                if (bytesRead == 0) break;

                await fileStream.WriteAsync(buffer, 0, bytesRead, token);
                fileStream.Flush();

                _downloadedBytes += bytesRead;
                speedBytes += bytesRead;

                // 每 500ms 更新一次速度
                var elapsedMs = stopwatch.ElapsedMilliseconds - lastSpeedUpdateMs;
                double currentSpeed;
                if (elapsedMs >= 500)
                {
                    currentSpeed = speedBytes * 1000.0 / elapsedMs;
                    speedBytes = 0;
                    lastSpeedUpdateMs = stopwatch.ElapsedMilliseconds;
                }
                else
                {
                    lock (_progressLock)
                    {
                        currentSpeed = _progress.Speed;
                    }
                }

                UpdateProgress(_downloadedBytes, currentSpeed);
                OnProgress?.Invoke(this, Progress);
            }
        }

        /// <summary>
        /// 根据 HTTP 响应更新总字节数
        /// </summary>
        private void UpdateTotalBytes(HttpWebResponse response)
        {
            var contentLength = response.ContentLength;

            if (response.StatusCode == HttpStatusCode.PartialContent && contentLength > 0)
            {
                // 服务器返回部分内容: 总大小 = 已下载 + 本次内容长度
                lock (_progressLock)
                {
                    _progress.TotalBytes = _downloadedBytes + contentLength;
                }
            }
            else if (contentLength > 0)
            {
                lock (_progressLock)
                {
                    _progress.TotalBytes = contentLength;
                }
            }
        }

        /// <summary>
        /// 更新进度数据
        /// </summary>
        private void UpdateProgress(long bytesDownloaded, double speed)
        {
            lock (_progressLock)
            {
                _progress.BytesDownloaded = bytesDownloaded;
                _progress.Speed = speed;
            }
        }

        /// <summary>
        /// 校验断点续传的文件状态
        /// </summary>
        private void ValidateResumeState()
        {
            if (_downloadedBytes <= 0) return;

            if (File.Exists(_savePath))
            {
                var fileLength = new FileInfo(_savePath).Length;
                if (fileLength != _downloadedBytes)
                    _downloadedBytes = fileLength;
            }
            else
            {
                _downloadedBytes = 0;
                _serverSupportsResume = false;
            }
        }

        /// <summary>
        /// 尝试延迟等待, 处理等待期间的暂停/取消
        /// </summary>
        /// <returns>true 表示等待成功, false 表示被暂停或取消</returns>
        private async Task<bool> TryDelayAsync(int delayMs, CancellationToken token)
        {
            try
            {
                await Task.Delay(delayMs, token);
                return true;
            }
            catch
            {
                if (_isPauseRequested)
                {
                    _isPauseRequested = false;
                    _status = EDownloadStatus.Paused;
                }
                else
                {
                    _status = EDownloadStatus.Cancelled;
                    OnCancelled?.Invoke(this);
                }
                return false;
            }
        }

        /// <summary>
        /// 中止当前请求并取消令牌
        /// </summary>
        private void Abort()
        {
            try { _currentRequest?.Abort(); }
            catch { /* ignored */ }
            try { _cts?.Cancel(); }
            catch { /* ignored */ }
        }

        /// <summary>
        /// 将异常分类为下载错误类型
        /// </summary>
        private static EDownloadError ClassifyException(Exception ex)
        {
            switch (ex)
            {
                case OperationCanceledException _:
                    return EDownloadError.Cancelled;

                case WebException webEx:
                    switch (webEx.Status)
                    {
                        case WebExceptionStatus.Timeout:
                            return EDownloadError.Timeout;
                        case WebExceptionStatus.NameResolutionFailure:
                        case WebExceptionStatus.ConnectFailure:
                        case WebExceptionStatus.ConnectionClosed:
                        case WebExceptionStatus.PipelineFailure:
                        case WebExceptionStatus.SendFailure:
                        case WebExceptionStatus.ReceiveFailure:
                        case WebExceptionStatus.KeepAliveFailure:
                            return EDownloadError.Network;
                        case WebExceptionStatus.ProtocolError:
                            return EDownloadError.Server;
                        default:
                            return EDownloadError.Network;
                    }

                case SocketException _:
                    return EDownloadError.Network;

                case UriFormatException _:
                case NotSupportedException _:
                    return EDownloadError.InvalidUrl;

                case IOException _:
                case UnauthorizedAccessException _:
                    return EDownloadError.Storage;

                default:
                    return EDownloadError.Unknown;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Dispose();
            _currentRequest = null;
        }

        #endregion
    }
}
