/*
 * datetime     : 2026/2/20
 * description  : 多任务并行下载器
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;

namespace ToolKit.Tools.Network
{
    /// <summary>
    /// 多任务并行下载器
    /// <para>采用单独的下载线程进行任务调度</para>
    /// <para>通过信号量控制最大并发下载数量</para>
    /// <para>支持动态添加任务、暂停全部、取消全部</para>
    /// </summary>
    public class ParallelDownloader : IDisposable
    {
        #region Fields

        private readonly ConcurrentQueue<DownloadTask> _pendingTasks
            = new ConcurrentQueue<DownloadTask>();
        private readonly List<DownloadTask> _activeTasks = new List<DownloadTask>();
        private readonly object _lock = new object();

        private int _maxConcurrency;
        private SemaphoreSlim _semaphore;
        private CancellationTokenSource _cts;
        private Thread _downloadThread;
        private volatile bool _isRunning;
        private IPlatformDownloadHandler _platformHandler;
        private bool _disposed;

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

        /// <summary> 最大并发下载数 </summary>
        public int MaxConcurrency => _maxConcurrency;

        /// <summary> 是否正在运行 </summary>
        public bool IsRunning => _isRunning;

        /// <summary> 等待中的任务数 </summary>
        public int PendingCount => _pendingTasks.Count;

        /// <summary> 正在下载的任务数 </summary>
        public int ActiveCount
        {
            get { lock (_lock) return _activeTasks.Count; }
        }

        /// <summary> 已完成的任务数 </summary>
        public int CompletedCount => _completedTaskCount;

        /// <summary> 总任务数 </summary>
        public int TotalCount => _totalTaskCount;

        /// <summary> 平台下载处理器 </summary>
        public IPlatformDownloadHandler PlatformHandler
        {
            get => _platformHandler;
            set => _platformHandler = value;
        }

        #endregion

        #region Constructor

        /// <summary>
        /// 创建并行下载器
        /// </summary>
        /// <param name="maxConcurrency">最大并发下载数</param>
        public ParallelDownloader(int maxConcurrency = 3)
        {
            _maxConcurrency = Math.Max(1, maxConcurrency);
            _semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 添加下载任务
        /// <para>如果下载器已启动, 任务将自动被调度执行</para>
        /// </summary>
        /// <param name="task">下载任务</param>
        public void AddTask(DownloadTask task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            _pendingTasks.Enqueue(task);
            Interlocked.Increment(ref _totalTaskCount);
        }

        /// <summary>
        /// 批量添加下载任务
        /// </summary>
        /// <param name="tasks">下载任务集合</param>
        public void AddTasks(IEnumerable<DownloadTask> tasks)
        {
            if (tasks == null) throw new ArgumentNullException(nameof(tasks));
            foreach (var task in tasks)
            {
                if (task != null)
                {
                    _pendingTasks.Enqueue(task);
                    Interlocked.Increment(ref _totalTaskCount);
                }
            }
        }

        /// <summary>
        /// 启动下载器
        /// <para>创建单独的下载线程进行任务调度和执行</para>
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _isRunning = true;

            // 通知平台处理器初始任务数 (确保进入后台时能正确识别有活跃任务)
            _platformHandler?.UpdateActiveTaskCount(0, _totalTaskCount);

            _downloadThread = new Thread(ThreadEntry)
            {
                IsBackground = true,
                Name = "ParallelDownloadThread"
            };
            _downloadThread.Start();
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
            }

            // 清空等待队列
            while (_pendingTasks.TryDequeue(out var task))
                task.Cancel();

            _cts?.Cancel();
        }

        /// <summary>
        /// 修改最大并发数 (仅在下载器未运行时有效)
        /// </summary>
        /// <param name="maxConcurrency">新的最大并发数</param>
        /// <exception cref="InvalidOperationException">下载器运行中时抛出</exception>
        public void SetMaxConcurrency(int maxConcurrency)
        {
            if (_isRunning)
                throw new InvalidOperationException("无法在下载器运行时修改并发数");

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

        #endregion

        #region Private Methods

        /// <summary>
        /// 下载线程入口
        /// </summary>
        private void ThreadEntry()
        {
            try
            {
                ProcessTasksAsync(_cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // 正常取消, 不做处理
            }
            catch (Exception ex)
            {
                Log.Error($"[ParallelDownloader] 下载线程异常: {ex}");
            }
            finally
            {
                _isRunning = false;
            }
        }

        /// <summary>
        /// 任务调度主循环
        /// </summary>
        private async Task ProcessTasksAsync(CancellationToken token)
        {
            var runningTasks = new List<Task>();
            var hasProcessedAny = false;

            while (!token.IsCancellationRequested)
            {
                var startedNew = false;

                // 从队列中取出任务并启动, 直到达到并发上限
                while (_pendingTasks.TryDequeue(out var downloadTask))
                {
                    token.ThrowIfCancellationRequested();
                    await _semaphore.WaitAsync(token);

                    lock (_lock) _activeTasks.Add(downloadTask);
                    runningTasks.Add(RunTaskAsync(downloadTask, token));

                    startedNew = true;
                    hasProcessedAny = true;
                }

                // 清理已完成的 Task
                for (int i = runningTasks.Count - 1; i >= 0; i--)
                {
                    if (runningTasks[i].IsCompleted)
                        runningTasks.RemoveAt(i);
                }

                if (runningTasks.Count > 0)
                {
                    // 等待任意一个任务完成, 然后重新检查队列
                    await Task.WhenAny(runningTasks);
                }
                else if (!startedNew)
                {
                    // 没有运行中的任务, 也没有新任务启动
                    if (hasProcessedAny)
                    {
                        hasProcessedAny = false;
                        OnAllCompleted?.Invoke();
                    }

                    // 等待新任务加入
                    await Task.Delay(100, token);
                }
            }

            // 等待所有活跃任务结束
            if (runningTasks.Count > 0)
            {
                try { await Task.WhenAll(runningTasks); }
                catch { /* 任务可能已被取消 */ }
            }
        }

        /// <summary>
        /// 执行单个下载任务, 完成后释放信号量
        /// </summary>
        private async Task RunTaskAsync(DownloadTask task, CancellationToken token)
        {
            try
            {
                await task.ExecuteAsync(token);

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
            
            // 等待下载线程退出
            if (_downloadThread != null && _downloadThread.IsAlive)
            {
                _downloadThread.Join(5000);
            }
            _downloadThread = null;
        }

        #endregion
    }
}
