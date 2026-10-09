/*
 * datetime     : 2026/2/20
 * description  : 简易下载器, 通过参数控制并发或顺序下载
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Network
{
    /// <summary>
    /// 简易下载器
    /// <para>通过 maxConcurrency 参数控制下载模式:</para>
    /// <para>  maxConcurrency = 1 → 顺序下载</para>
    /// <para>  maxConcurrency > 1 → 并行下载</para>
    /// <para>支持暂停/恢复/取消, 支持断点续传</para>
    /// </summary>
    public class SimpleDownloader : IDisposable
    {
        #region Fields

        private readonly Queue<DownloadTask> _pendingTasks = new Queue<DownloadTask>();
        private readonly List<DownloadTask> _activeTasks = new List<DownloadTask>();
        private readonly List<TaskCompletionSource<bool>> _resumeSignals
            = new List<TaskCompletionSource<bool>>();
        private readonly object _lock = new object();

        private int _maxConcurrency;
        private SemaphoreSlim _semaphore;
        private CancellationTokenSource _cts;
        private Task _processTask;
        private volatile bool _isRunning;
        private IPlatformDownloadHandler _platformHandler;
        private bool _disposed;

        // 直接可等待入口 (DownloadAsync) 的状态：与旧队列入口互斥
        private readonly NetworkOptions _directOptions;
        private readonly SemaphoreSlim _directSlots;
        private int _directMode;    // 0=未用, 1=使用直接入口, 2=使用旧队列
        private readonly object _modeLock = new object();

        private int _totalTaskCount;
        private int _completedTaskCount;

        #endregion

        #region Callbacks

        /// <summary> 所有任务完成回调 </summary>
        public Action OnAllCompleted { get; set; }

        /// <summary> 任务数量进度回调 (参数: 已完成数, 总数) </summary>
        public Action<int, int> OnTaskCountChanged { get; set; }

        #endregion

        #region Properties

        /// <summary> 最大并发下载数 (1=顺序下载) </summary>
        public int MaxConcurrency => _maxConcurrency;

        /// <summary> 是否正在运行 </summary>
        public bool IsRunning => _isRunning;

        /// <summary> 等待中的任务数 </summary>
        public int PendingCount
        {
            get { lock (_lock) return _pendingTasks.Count; }
        }

        /// <summary> 正在下载的任务数 </summary>
        public int ActiveCount
        {
            get { lock (_lock) return _activeTasks.Count; }
        }

        /// <summary> 已完成的任务数 </summary>
        public int CompletedCount => _completedTaskCount;

        /// <summary> 总任务数 </summary>
        public int TotalCount => _totalTaskCount;

        /// <summary> 平台下载处理器 (移动平台后台下载/通知栏) </summary>
        public IPlatformDownloadHandler PlatformHandler
        {
            get => _platformHandler;
            set => _platformHandler = value;
        }

        #endregion

        #region Constructor

        /// <summary>
        /// 创建简易下载器
        /// </summary>
        /// <param name="maxConcurrency">最大并发数 (1=顺序下载, >1=并行下载)</param>
        public SimpleDownloader(int maxConcurrency = 1)
            : this(maxConcurrency, null)
        {
        }

        /// <summary>
        /// 创建简易下载器 (直接可等待入口使用此配置的并发、超时与重试；旧构造映射一致默认值)
        /// </summary>
        public SimpleDownloader(int maxConcurrency, NetworkOptions? directOptions)
        {
            _maxConcurrency = Math.Max(1, maxConcurrency);
            _semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
            _directOptions = (directOptions ?? new NetworkOptions()).Clone();
            _directOptions.MaxConcurrentDownloads = Math.Min(
                Math.Max(1, _directOptions.MaxConcurrentDownloads), _maxConcurrency);
            _directOptions.Validate();
            _directSlots = new SemaphoreSlim(_directOptions.MaxConcurrentDownloads,
                _directOptions.MaxConcurrentDownloads);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 直接可等待的一次完整下载：成功返回表示文件已写完、响应与写入句柄已关闭；
        /// 失败使 Task 失败 (DownloadException)；取消使 Task 取消且实际 I/O 已停止。
        /// 固定从头写入，网络重试全部包含在本调用内。受实例并发限制，排队等待槽位也响应取消。
        /// 与旧 AddTask/Start 队列互斥：同一实例开始使用一种入口后不切换模式。
        /// </summary>
        public async Task DownloadAsync(
            DownloadRequest request, string destinationPath, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrEmpty(destinationPath)) throw new ArgumentException("destinationPath 不能为空");
            _EnsureDirectMode();
            _EnsureNotDisposed();

            await _directSlots.WaitAsync(cancellationToken).ConfigureAwait(false); // 排队等待槽位可取消
            try
            {
                await DownloadTask.ExecuteDirectAsync(request, destinationPath, _directOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _directSlots.Release();
            }
        }

        /// <summary>
        /// 创建下载任务
        /// </summary>
        /// <param name="url">下载地址</param>
        /// <param name="savePath">文件保存路径</param>
        /// <param name="maxRetries">最大重试次数</param>
        /// <param name="retryDelayMs">重试间隔 (毫秒)</param>
        /// <param name="bufferSize">读写缓冲区大小</param>
        /// <param name="connectTimeoutMs">连接超时时间 (毫秒)</param>
        /// <param name="readTimeoutMs">读取超时时间 (毫秒)</param>
        /// <param name="tag">任务标签, 为 null 时自动使用文件名</param>
        /// <returns>新创建的下载任务</returns>
        public DownloadTask CreateTask(string url, string savePath,
            int maxRetries = 3, int retryDelayMs = 1000,
            int bufferSize = 8192,
            int connectTimeoutMs = 30000, int readTimeoutMs = 30000,
            long maxBytesPerSecond = 0,
            string tag = null)
        {
            return new DownloadTask(url, savePath, maxRetries, retryDelayMs,
                bufferSize, connectTimeoutMs, readTimeoutMs, maxBytesPerSecond, tag);
        }

        /// <summary>
        /// 添加下载任务
        /// </summary>
        /// <param name="task">下载任务</param>
        public void AddTask(DownloadTask task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            _EnsureQueueMode();

            lock (_lock)
            {
                _pendingTasks.Enqueue(task);
                _totalTaskCount++;
            }
        }

        /// <summary>
        /// 批量添加下载任务
        /// </summary>
        /// <param name="tasks">下载任务集合</param>
        public void AddTasks(IEnumerable<DownloadTask> tasks)
        {
            if (tasks == null) throw new ArgumentNullException(nameof(tasks));

            lock (_lock)
            {
                foreach (var task in tasks)
                {
                    if (task != null)
                    {
                        _pendingTasks.Enqueue(task);
                        _totalTaskCount++;
                    }
                }
            }
        }

        /// <summary>
        /// 启动下载
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;
            _EnsureQueueMode();

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _isRunning = true;

            // 通知平台处理器初始任务数 (确保进入后台时能正确识别有活跃任务)
            _platformHandler?.UpdateActiveTaskCount(0, _totalTaskCount);

            var token = _cts.Token;
            _processTask = Task.Run(() => ProcessTasksAsync(token));
        }

        /// <summary>
        /// 暂停所有正在下载的任务
        /// </summary>
        public void PauseAll()
        {
            lock (_lock)
            {
                foreach (var task in _activeTasks)
                    task.Pause();
            }
        }

        /// <summary>
        /// 恢复所有已暂停的任务继续下载
        /// </summary>
        public void ResumeAll()
        {
            lock (_lock)
            {
                foreach (var signal in _resumeSignals)
                    signal.TrySetResult(true);
                _resumeSignals.Clear();
            }
        }

        /// <summary>
        /// 取消所有任务并停止下载器
        /// </summary>
        public void CancelAll()
        {
            _isRunning = false;

            lock (_lock)
            {
                foreach (var task in _activeTasks)
                    task.Cancel();
                _activeTasks.Clear();
                _pendingTasks.Clear();

                foreach (var signal in _resumeSignals)
                    signal.TrySetCanceled();
                _resumeSignals.Clear();
            }

            _cts?.Cancel();
        }

        /// <summary>
        /// 修改最大并发数 (仅在未运行时有效)
        /// </summary>
        /// <param name="maxConcurrency">新的最大并发数</param>
        /// <exception cref="InvalidOperationException">运行中时抛出</exception>
        public void SetMaxConcurrency(int maxConcurrency)
        {
            if (_isRunning)
                throw new InvalidOperationException("无法在下载器运行时修改并发数");
            if (Volatile.Read(ref _directMode) == 1)
                throw new InvalidOperationException("直接下载入口已使用固定并发配置，不能再修改");

            _maxConcurrency = Math.Max(1, maxConcurrency);
            _semaphore?.Dispose();
            _semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        }

        /// <summary>
        /// 获取所有活跃任务的快照
        /// </summary>
        public IDownloadTask[] GetActiveTasks()
        {
            lock (_lock)
            {
                return _activeTasks.ToArray();
            }
        }

        /// <summary>
        /// 重置任务计数器
        /// </summary>
        public void ResetCounters()
        {
            _totalTaskCount = 0;
            _completedTaskCount = 0;
        }

        #endregion

        #region Private Methods

        private void _EnsureDirectMode()
        {
            lock (_modeLock)
            {
                if (_directMode == 2)
                {
                    throw new InvalidOperationException(
                        "同一实例不能混用直接下载入口与旧任务队列；资源加载应使用独立的 SimpleDownloader 实例");
                }
                _directMode = 1;
            }
        }

        private void _EnsureQueueMode()
        {
            lock (_modeLock)
            {
                if (_directMode == 1)
                {
                    throw new InvalidOperationException(
                        "同一实例不能混用直接下载入口与旧任务队列；资源加载应使用独立的 SimpleDownloader 实例");
                }
                _directMode = 2;
            }
        }

        private void _EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SimpleDownloader));
            }
        }

        /// <summary>
        /// 任务调度主循环
        /// </summary>
        private async Task ProcessTasksAsync(CancellationToken token)
        {
            var runningTasks = new List<Task>();

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var startedNew = false;

                    // 从队列中取出任务并启动, 直到达到并发上限
                    while (true)
                    {
                        lock (_lock)
                        {
                            if (_pendingTasks.Count == 0) break;
                        }

                        await _semaphore.WaitAsync(token);

                        DownloadTask downloadTask;
                        lock (_lock)
                        {
                            if (_pendingTasks.Count == 0)
                            {
                                _semaphore.Release();
                                break;
                            }
                            downloadTask = _pendingTasks.Dequeue();
                            _activeTasks.Add(downloadTask);
                        }

                        runningTasks.Add(RunTaskAsync(downloadTask, token));
                        startedNew = true;
                    }

                    // 清理已完成的 Task
                    for (int i = runningTasks.Count - 1; i >= 0; i--)
                    {
                        if (runningTasks[i].IsCompleted)
                            runningTasks.RemoveAt(i);
                    }

                    if (runningTasks.Count > 0)
                    {
                        await Task.WhenAny(runningTasks);
                    }
                    else if (!startedNew)
                    {
                        if (_completedTaskCount > 0)
                        {
                            OnAllCompleted?.Invoke();
                        }
                        break;
                    }
                }

                // 等待所有活跃任务结束
                if (runningTasks.Count > 0)
                {
                    try { await Task.WhenAll(runningTasks); }
                    catch { /* 任务可能已被取消 */ }
                }
            }
            finally
            {
                _isRunning = false;
            }
        }

        /// <summary>
        /// 执行单个下载任务, 支持暂停/恢复, 完成后释放信号量和更新计数
        /// </summary>
        private async Task RunTaskAsync(DownloadTask task, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await task.ExecuteAsync(token);

                    if (task.Status != EDownloadStatus.Paused) break;

                    // 暂停后等待恢复信号
                    var resumeSignal = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                    lock (_lock)
                    {
                        _resumeSignals.Add(resumeSignal);
                    }

                    try
                    {
                        using (token.Register(() => resumeSignal.TrySetCanceled()))
                        {
                            await resumeSignal.Task;
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }

                    // 恢复后继续循环, 断点续传
                }

                // 更新完成计数
                var completed = Interlocked.Increment(ref _completedTaskCount);
                OnTaskCountChanged?.Invoke(completed, _totalTaskCount);
                _platformHandler?.UpdateActiveTaskCount(completed, _totalTaskCount);
            }
            finally
            {
                lock (_lock) _activeTasks.Remove(task);
                _semaphore.Release();
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            
            _disposed = true;

            CancelAll();
            _semaphore?.Dispose();
            _cts?.Dispose();
            _directSlots?.Dispose();
        }

        #endregion
    }
}
