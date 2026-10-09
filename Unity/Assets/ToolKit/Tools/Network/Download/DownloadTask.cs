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
        private readonly long _maxBytesPerSecond;
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

        /// <summary> 最大下载速度(字节/秒), 小于等于 0 表示不限速 </summary>
        public long MaxBytesPerSecond => _maxBytesPerSecond;

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
            long maxBytesPerSecond = 0,
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
            _maxBytesPerSecond = Math.Max(0, maxBytesPerSecond);
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

        #region 可直接等待的执行路径 (供 SimpleDownloader.DownloadAsync 复用)

        /// <summary>
        /// 执行一次完整下载并传播真实结果：成功返回表示文件已写完、响应与写入句柄已关闭；
        /// 失败抛 DownloadException；取消抛 OperationCanceledException。
        /// 固定从头写入：每次尝试 (含重试) 重新截断目标文件，禁止 Range/Append；
        /// 不支持暂停，也不触碰实例的续传状态。只有瞬态网络故障进入重试。
        /// </summary>
        internal static async Task ExecuteDirectAsync(
            DownloadRequest request, string destinationPath, NetworkOptions options,
            CancellationToken cancellationToken)
        {
            var maxAttempts = 1 + Math.Max(0, options.MaxRetries);
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                FileStream? writeStream = null;
                HttpWebResponse? response = null;
                try
                {
                    var httpRequest = (HttpWebRequest)WebRequest.Create(request.Url);
                    httpRequest.Method = "GET";
                    httpRequest.AllowAutoRedirect = request.AllowRedirects;
                    httpRequest.MaximumAutomaticRedirections = request.MaxRedirects;
                    if (request.Headers != null)
                    {
                        foreach (var header in request.Headers)
                        {
                            httpRequest.Headers.Set(header.Key, header.Value);
                        }
                    }

                    // 连接/响应头阶段超时：HttpWebRequest.Timeout 不约束异步路径，用取消令牌实施
                    HttpWebResponse obtained;
                    using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        connectCts.CancelAfter(options.ConnectTimeout);
                        using (connectCts.Token.Register(() =>
                               {
                                   try { httpRequest.Abort(); }
                                   catch { /* ignored */ }
                               }))
                        {
                            try
                            {
                                obtained = (HttpWebResponse)await httpRequest.GetResponseAsync().ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                                     && connectCts.IsCancellationRequested)
                            {
                                throw new DownloadException(EDownloadError.Timeout, "等待连接或响应头超时");
                            }
                            catch (WebException webEx) when (connectCts.IsCancellationRequested
                                                             && !cancellationToken.IsCancellationRequested)
                            {
                                throw new DownloadException(EDownloadError.Timeout, "等待连接或响应头超时", null, webEx);
                            }
                        }
                    }
                    response = obtained;

                    if ((int)response.StatusCode >= 400)
                    {
                        throw ClassifyHttpStatus((int)response.StatusCode);
                    }

                    var directory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // 从头写入：每次尝试都截断，不叠写、不隐式发送续传 Range
                    writeStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        Math.Max(1024, options.BufferSize), useAsync: true);
                    using (var responseStream = response.GetResponseStream())
                    {
                        if (responseStream == null)
                        {
                            throw new DownloadException(EDownloadError.Network, "响应流为空");
                        }
                        await CopyStreamWithReadTimeoutAsync(responseStream, writeStream,
                            Math.Max(1024, options.BufferSize), options.ResponseTimeout, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    await writeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    writeStream.Dispose();
                    writeStream = null;
                    response.Dispose();
                    response = null;
                    return; // 成功：句柄已全部关闭
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // 调用者取消：实际 I/O 已随句柄释放停止
                }
                catch (WebException webCancelEx) when (cancellationToken.IsCancellationRequested)
                {
                    // 令牌触发的请求中止：以取消 Task 语义浮出，不包装成网络故障
                    throw new OperationCanceledException(
                        "下载已取消，底层 I/O 已停止", webCancelEx, cancellationToken);
                }
                catch (DownloadException ex)
                {
                    EnsureClosed(ref writeStream, ref response);
                    if (!IsTransientDownloadFailure(ex) || attempt >= maxAttempts)
                    {
                        throw;
                    }
                }
                catch (WebException webEx)
                {
                    EnsureClosed(ref writeStream, ref response);
                    var (kind, status, reason) = ClassifyWebException(webEx);
                    var mapped = new DownloadException(kind, reason, status, webEx);
                    if (!IsTransientDownloadFailure(mapped) || attempt >= maxAttempts)
                    {
                        throw mapped;
                    }
                }
                catch (Exception ex)
                {
                    EnsureClosed(ref writeStream, ref response);
                    throw ClassifyLocalFailure(ex); // 写盘/权限等：不重试
                }
                finally
                {
                    // 兜底关闭 (异常路径)
                    writeStream?.Dispose();
                    response?.Dispose();
                }

                await Task.Delay(TimeSpan.FromSeconds(options.RetryBaseDelay.TotalSeconds * Math.Pow(2, attempt - 1)),
                    cancellationToken).ConfigureAwait(false);
            }
            throw new DownloadException(EDownloadError.Unknown, "下载重试循环不可达");
        }

        /// <summary> 响应体逐块读取：单次读取超过 readTimeout 未返回即超时中断 (异步路径的实际读超时) </summary>
        private static async Task CopyStreamWithReadTimeoutAsync(
            Stream source, Stream destination, int bufferSize, TimeSpan readTimeout, CancellationToken ct)
        {
            var buffer = new byte[bufferSize];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(readTimeout);
                int read;
                try
                {
                    read = await source.ReadAsync(buffer, 0, buffer.Length, readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested
                                                         && readCts.IsCancellationRequested)
                {
                    throw new DownloadException(EDownloadError.Timeout, "读取响应体超时");
                }
                if (read <= 0)
                {
                    return;
                }
                await destination.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
            }
        }

        private static void EnsureClosed(ref FileStream? writeStream, ref HttpWebResponse? response)
        {
            try { writeStream?.Dispose(); } catch { /* ignored */ }
            try { response?.Dispose(); } catch { /* ignored */ }
            writeStream = null;
            response = null;
        }

        internal static DownloadException ClassifyHttpStatus(int status)
        {
            var kind = status == 404 || status == 410
                ? EDownloadError.NotFound
                : status == 401 || status == 403
                    ? EDownloadError.AccessDenied
                    : status == 429
                        ? EDownloadError.ServerBusy
                        : EDownloadError.Server;
            var reason = status == 429 ? "服务器限流 (429)" : $"HTTP 状态错误 ({status})";
            return new DownloadException(kind, reason, status);
        }

        private static (EDownloadError kind, int? status, string reason) ClassifyWebException(WebException webEx)
        {
            switch (webEx.Status)
            {
                case WebExceptionStatus.Timeout:
                    return (EDownloadError.Timeout, null, "网络超时");
                case WebExceptionStatus.NameResolutionFailure:
                    return (EDownloadError.Network, null, "域名解析失败");
                case WebExceptionStatus.SecureChannelFailure:
                    return (EDownloadError.Network, null, "TLS 握手失败");
                case WebExceptionStatus.ConnectFailure:
                    return (EDownloadError.Network, null, "连接建立失败");
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.KeepAliveFailure:
                case WebExceptionStatus.PipelineFailure:
                case WebExceptionStatus.SendFailure:
                    return (EDownloadError.Network, null, "连接中断");
                case WebExceptionStatus.ProtocolError:
                    if (webEx.Response is HttpWebResponse http && (int)http.StatusCode >= 400)
                    {
                        var status = (int)http.StatusCode;
                        var classified = ClassifyHttpStatus(status);
                        return (classified.ErrorKind, classified.HttpStatus!.Value, classified.Reason);
                    }
                    return (EDownloadError.Server, null, "协议错误");
                case WebExceptionStatus.RequestCanceled:
                    return (EDownloadError.Cancelled, null, "请求已中止");
                default:
                    return (EDownloadError.Network, null, $"网络错误 ({webEx.Status})");
            }
        }

        private static DownloadException ClassifyLocalFailure(Exception ex)
        {
            if (ex is UnauthorizedAccessException)
            {
                return new DownloadException(EDownloadError.Storage, "无写入权限", null, ex);
            }
            if (ex is IOException || ex is DirectoryNotFoundException)
            {
                return new DownloadException(EDownloadError.Storage, "写入目标文件失败", null, ex);
            }
            if (ex is UriFormatException || ex is NotSupportedException)
            {
                return new DownloadException(EDownloadError.InvalidUrl, "下载地址非法", null, ex);
            }
            return new DownloadException(EDownloadError.Unknown, "未预期的下载失败", null, ex);
        }

        /// <summary> 只有瞬态网络故障适合重试：地址错误、404、授权、存储与取消不重试 </summary>
        private static bool IsTransientDownloadFailure(DownloadException ex)
        {
            switch (ex.ErrorKind)
            {
                case EDownloadError.Timeout:
                case EDownloadError.Network:
                case EDownloadError.ServerBusy:
                case EDownloadError.Server:
                    return true;
                default:
                    return false;
            }
        }

        #endregion

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
            var throttleBytes = 0L;
            var throttleWindowStartMs = stopwatch.ElapsedMilliseconds;

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
                throttleBytes += bytesRead;

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

                var throttle = await ThrottleIfNeededAsync(
                    stopwatch,
                    throttleBytes,
                    throttleWindowStartMs,
                    token);
                throttleBytes = throttle.bytes;
                throttleWindowStartMs = throttle.windowStartMs;
            }
        }

        private async Task<(long bytes, long windowStartMs)> ThrottleIfNeededAsync(
            Stopwatch stopwatch,
            long throttleBytes,
            long throttleWindowStartMs,
            CancellationToken token)
        {
            if (_maxBytesPerSecond <= 0 || throttleBytes <= 0)
            {
                return (throttleBytes, throttleWindowStartMs);
            }

            var expectedMs = throttleBytes * 1000.0 / _maxBytesPerSecond;
            var elapsedMs = stopwatch.ElapsedMilliseconds - throttleWindowStartMs;
            if (expectedMs > elapsedMs)
            {
                var delayMs = expectedMs - elapsedMs;
                await Task.Delay((int)Math.Min(delayMs, int.MaxValue), token);
            }

            var windowMs = stopwatch.ElapsedMilliseconds - throttleWindowStartMs;
            if (windowMs >= 1000)
            {
                return (0, stopwatch.ElapsedMilliseconds);
            }
            return (throttleBytes, throttleWindowStartMs);
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
