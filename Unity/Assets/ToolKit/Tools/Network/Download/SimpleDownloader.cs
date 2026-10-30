/*
 * datetime     : 2026/2/20
 * description  : 简易下载器, 通过并发数控制顺序或并行下载
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;

namespace ToolKit.Tools.Network
{
    /// <summary>
    /// 简易下载器
    /// <para>通过 <see cref="MaxConcurrency"/> 控制顺序 (1) 或并行下载, 运行中修改立即生效</para>
    /// <para>支持暂停/恢复/取消与断点续传</para>
    /// <para>启动后追加的任务会被继续调度; 队列清空时触发一次 <see cref="OnAllCompleted"/></para>
    /// <para>下载策略参数集中在 <see cref="DownloadOptions"/>, <see cref="CreateTask"/> 默认沿用下载器的选项</para>
    /// </summary>
    public class SimpleDownloader : IDisposable
    {
        #region Fields

        private readonly Queue<DownloadTask> _pendingTasks = new Queue<DownloadTask>();
        private readonly List<DownloadTask> _activeTasks = new List<DownloadTask>();
        private readonly List<DownloadTask> _pausedTasks = new List<DownloadTask>();
        private readonly object _lock = new object();
        private readonly DownloadOptions _options;

        private int _maxConcurrency;
        private CancellationTokenSource? _cts;
        private Task? _processTask;
        private int _runId;
        private volatile bool _isRunning;
        private volatile bool _isPaused;
        private IPlatformDownloadHandler? _platformHandler;
        private bool _disposed;

        private int _totalTaskCount;
        private int _completedTaskCount;
        private int _finishedSinceNotify;

        private TaskCompletionSource<bool> _workSignal = CreateSignal();

        #endregion

        #region Callbacks

        /// <summary> 队列清空回调 (每批任务全部结束后触发一次) </summary>
        public Action? OnAllCompleted { get; set; }

        /// <summary> 任务数量进度回调 (参数: 已完成数, 总数) </summary>
        public Action<int, int>? OnTaskCountChanged { get; set; }

        #endregion

        #region Properties

        /// <summary> 最大并发下载数 (1=顺序下载); 运行中修改会在下一次调度时生效 </summary>
        public int MaxConcurrency
        {
            get => _maxConcurrency;
            set
            {
                var clamped = Math.Max(1, value);
                if (clamped == _maxConcurrency) return;

                _maxConcurrency = clamped;
                SignalWork();
            }
        }

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
        public IPlatformDownloadHandler? PlatformHandler
        {
            get => _platformHandler;
            set => _platformHandler = value;
        }

        #endregion

        #region Constructor

        /// <summary>
        /// 创建下载器
        /// </summary>
        /// <param name="maxConcurrency">最大并发数 (1=顺序下载, &gt;1=并行下载)</param>
        /// <param name="options">创建任务时使用的默认下载策略, 为 null 时使用默认值</param>
        public SimpleDownloader(int maxConcurrency = 1, DownloadOptions? options = null)
        {
            _maxConcurrency = Math.Max(1, maxConcurrency);
            _options = options?.Copy() ?? new DownloadOptions();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 创建下载任务
        /// </summary>
        /// <param name="url">下载地址</param>
        /// <param name="savePath">文件保存路径</param>
        /// <param name="tag">任务标签, 为 null 时自动使用文件名</param>
        /// <param name="options">该任务单独使用的下载策略, 为 null 时使用下载器的默认选项</param>
        /// <returns>新创建的下载任务</returns>
        public DownloadTask CreateTask(string url, string savePath, string? tag = null, DownloadOptions? options = null)
        {
            return new DownloadTask(url, savePath, options ?? _options, tag);
        }

        /// <summary>
        /// 添加下载任务
        /// <para>下载器已启动时任务会被自动调度</para>
        /// </summary>
        /// <param name="task">下载任务</param>
        public void AddTask(DownloadTask task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            lock (_lock)
            {
                _pendingTasks.Enqueue(task);
                _totalTaskCount++;
            }

            SignalWork();
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
                    if (task == null) continue;
                    _pendingTasks.Enqueue(task);
                    _totalTaskCount++;
                }
            }

            SignalWork();
        }

        /// <summary>
        /// 启动下载
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            DisposeCtsWhenDone(_processTask, _cts);
            _cts = new CancellationTokenSource();
            _isRunning = true;
            _isPaused = false;

            // 通知平台处理器初始任务数 (确保进入后台时能正确识别有活跃任务)
            _platformHandler?.UpdateActiveTaskCount(0, _totalTaskCount);

            var token = _cts.Token;
            var runId = ++_runId;
            _processTask = Task.Run(() => ProcessTasksAsync(token, runId));
        }

        /// <summary>
        /// 暂停所有正在下载的任务, 并停止启动新任务
        /// <para>已下载的数据保留, <see cref="ResumeAll"/> 后断点续传</para>
        /// </summary>
        public void PauseAll()
        {
            _isPaused = true;

            DownloadTask[] active;
            lock (_lock) active = _activeTasks.ToArray();

            foreach (var task in active)
                task.Pause();
        }

        /// <summary>
        /// 恢复所有已暂停的任务继续下载
        /// </summary>
        public void ResumeAll()
        {
            lock (_lock)
            {
                _isPaused = false;
                foreach (var task in _pausedTasks)
                    _pendingTasks.Enqueue(task);
                _pausedTasks.Clear();
            }

            SignalWork();
        }

        /// <summary>
        /// 取消所有任务并停止下载器
        /// </summary>
        public void CancelAll()
        {
            _isRunning = false;
            _isPaused = false;

            List<DownloadTask> tasks;
            lock (_lock)
            {
                tasks = new List<DownloadTask>(_activeTasks.Count + _pendingTasks.Count + _pausedTasks.Count);
                tasks.AddRange(_activeTasks);
                tasks.AddRange(_pendingTasks);
                tasks.AddRange(_pausedTasks);

                _activeTasks.Clear();
                _pendingTasks.Clear();
                _pausedTasks.Clear();
            }

            foreach (var task in tasks)
                task.Cancel();

            _cts?.Cancel();
        }

        /// <summary>
        /// 获取正在下载的任务快照 (不含已暂停与等待中的任务)
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

        /// <summary>
        /// 任务调度主循环: 有空位就启动任务, 队列清空后等待新任务
        /// </summary>
        private async Task ProcessTasksAsync(CancellationToken token, int runId)
        {
            var running = new List<Task>();

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!_isPaused)
                    {
                        while (TryStartNextTask(running, token))
                        {
                        }
                    }

                    if (token.IsCancellationRequested) break;

                    running.RemoveAll(task => task.IsCompleted);

                    if (running.Count > 0)
                    {
                        // 等待任一任务结束, 或新任务/并发变更唤醒调度器
                        bool canStartMore;
                        Task wake;
                        lock (_lock)
                        {
                            canStartMore = !_isPaused && _pendingTasks.Count > 0 &&
                                           _activeTasks.Count < _maxConcurrency;
                            wake = _workSignal.Task;
                        }

                        if (canStartMore) continue;

                        await WaitForSignalAsync(Task.WhenAny(Task.WhenAny(running), wake), token);
                        continue;
                    }

                    if (TryNotifyDrained())
                    {
                        continue;
                    }

                    await WaitForSignalAsync(WaitForWorkAsync(), token);
                }
            }
            catch (OperationCanceledException)
            {
                // 取消后正常退出
            }
            catch (Exception ex)
            {
                Log.Error($"[SimpleDownloader] 下载调度异常: {ex}");
            }
            finally
            {
                if (runId == _runId)
                {
                    _isRunning = false;
                }

                if (running.Count > 0)
                {
                    try { await Task.WhenAll(running); }
                    catch
                    {
                        // 任务取消或失败已由任务自身回调处理
                    }
                }
            }
        }

        /// <summary>
        /// 在并发上限内取出下一个任务并开始执行
        /// </summary>
        private bool TryStartNextTask(List<Task> running, CancellationToken token)
        {
            DownloadTask task;
            lock (_lock)
            {
                if (token.IsCancellationRequested || _isPaused ||
                    _activeTasks.Count >= _maxConcurrency || _pendingTasks.Count == 0)
                    return false;

                task = _pendingTasks.Dequeue();
                _activeTasks.Add(task);
            }

            running.Add(RunTaskAsync(task, token));
            return true;
        }

        /// <summary>
        /// 执行单个下载任务; 暂停的任务移入暂停队列, 等待恢复后重新入队
        /// </summary>
        private async Task RunTaskAsync(DownloadTask task, CancellationToken token)
        {
            try
            {
                await task.ExecuteAsync(token);

                if (task.Status == EDownloadStatus.Paused)
                {
                    lock (_lock) _pausedTasks.Add(task);
                    return;
                }

                Interlocked.Increment(ref _finishedSinceNotify);
                var completed = Interlocked.Increment(ref _completedTaskCount);
                OnTaskCountChanged?.Invoke(completed, _totalTaskCount);
                _platformHandler?.UpdateActiveTaskCount(completed, _totalTaskCount);
            }
            finally
            {
                lock (_lock) _activeTasks.Remove(task);
            }
        }

        /// <summary>
        /// 队列与活跃任务都为空时触发一次完成回调
        /// </summary>
        private bool TryNotifyDrained()
        {
            if (_isPaused) return false;

            lock (_lock)
            {
                if (_pendingTasks.Count > 0 || _pausedTasks.Count > 0 || _activeTasks.Count > 0)
                    return false;
            }

            if (Interlocked.Exchange(ref _finishedSinceNotify, 0) == 0)
                return false;

            OnAllCompleted?.Invoke();
            return true;
        }

        /// <summary>
        /// 唤醒调度器的信号; 每次唤醒都换用新的信号, 避免丢失唤醒
        /// </summary>
        private void SignalWork()
        {
            TaskCompletionSource<bool> signal;
            lock (_lock)
            {
                signal = _workSignal;
                _workSignal = CreateSignal();
            }

            signal.TrySetResult(true);
        }

        /// <summary>
        /// 等待新任务或恢复运行的信号; 队列中已有可执行任务时立即返回
        /// </summary>
        private Task WaitForWorkAsync()
        {
            lock (_lock)
            {
                if (!_isPaused && _pendingTasks.Count > 0) return Task.CompletedTask;
                return _workSignal.Task;
            }
        }

        /// <summary>
        /// 等待信号完成, 取消时抛出 <see cref="OperationCanceledException"/>
        /// </summary>
        private static async Task WaitForSignalAsync(Task signal, CancellationToken token)
        {
            if (signal.IsCompleted) return;

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                await Task.WhenAny(signal, cancelled.Task);
            }

            token.ThrowIfCancellationRequested();
        }

        private static TaskCompletionSource<bool> CreateSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// 调度任务结束后释放令牌源, 避免其仍被注册回调时被提前释放
        /// </summary>
        private static void DisposeCtsWhenDone(Task? processTask, CancellationTokenSource? cts)
        {
            if (cts == null) return;

            if (processTask == null || processTask.IsCompleted)
            {
                cts.Dispose();
                return;
            }

            processTask.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
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
            DisposeCtsWhenDone(_processTask, _cts);
        }

        #endregion
    }
}
