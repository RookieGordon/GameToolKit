using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 业务方持有的只读票据。每次成功提交对应一张票据。
    /// 可以在同一个领取窗口多次等待，不会重复执行任务。
    /// </summary>
    public sealed class TaskTicket<T>
    {
        public Guid Id { get; private set; }
        public bool IsCompleted { get { return Completion.IsCompleted; } }

        internal object Owner { get; private set; }
        internal Task<T> Completion { get; private set; }

        internal TaskTicket(Guid id, object owner, Task<T> completion)
        {
            Id = id;
            Owner = owner;
            Completion = completion;
        }
    }

    /// <summary>
    /// 统一领取窗口。方案 A：等待票据内部关联的 Task，不维护结果字典。
    /// 窗口不主动切换线程；调用方从主线程使用普通 await 恢复主线程上下文。
    /// </summary>
    public sealed class ResultWindow
    {
        private readonly object _owner;
        private readonly Action _verifyBusinessThread;

        internal ResultWindow(object owner, Action verifyBusinessThread)
        {
            _owner = owner;
            _verifyBusinessThread = verifyBusinessThread;
        }

        public Task<T> WaitAsync<T>(TaskTicket<T> ticket)
        {
            if (_verifyBusinessThread != null)
                _verifyBusinessThread();

            ValidateTicket(ticket);
            return ticket.Completion;
        }

        public Task<T[]> WaitAllAsync<T>(IEnumerable<TaskTicket<T>> tickets)
        {
            if (_verifyBusinessThread != null)
                _verifyBusinessThread();

            if (tickets == null)
                throw new ArgumentNullException(nameof(tickets));

            var pending = new List<Task<T>>();
            foreach (TaskTicket<T> ticket in tickets)
            {
                ValidateTicket(ticket);
                pending.Add(ticket.Completion);
            }

            // 返回结果按传入票据顺序排列，执行和完成顺序可以不同。
            return Task.WhenAll(pending);
        }

        private void ValidateTicket<T>(TaskTicket<T> ticket)
        {
            if (ticket == null)
                throw new ArgumentNullException(nameof(ticket));

            if (!ReferenceEquals(ticket.Owner, _owner))
                throw new ArgumentException("这张票据不属于当前领取窗口。", nameof(ticket));
        }
    }

    /// <summary>
    /// 一个共享队列，M 个相同消费者，全局最多 K 条任务处于执行阶段。
    /// 仅依赖 .NET Standard 2.1。核心代码没有 UnityEngine 依赖。
    /// </summary>
    public sealed class TicketScheduler : IAsyncDisposable
    {
        private interface IWorkItem
        {
            Task ExecuteAsync();
        }

        /// <summary>
        /// 内部工作单；只有执行层能完成票据。
        /// Queue 中通过接口保存工作单，允许同一队列处理不同结果类型。
        /// </summary>
        private sealed class WorkItem<T> : IWorkItem
        {
            private readonly Func<CancellationToken, Task<T>> _work;
            private readonly CancellationToken _cancellationToken;
            private readonly TaskCompletionSource<T> _completion;

            public TaskTicket<T> Ticket { get; private set; }

            public WorkItem(
                object owner,
                Func<CancellationToken, Task<T>> work,
                CancellationToken cancellationToken)
            {
                _work = work;
                _cancellationToken = cancellationToken;
                _completion = new TaskCompletionSource<T>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                // 票据和工作单关联同一个完成对象，不需要按编号搜索匹配。
                Ticket = new TaskTicket<T>(Guid.NewGuid(), owner, _completion.Task);
            }

            public async Task ExecuteAsync()
            {
                try
                {
                    // 排队期间取消：领取时检查，跳过实际操作。
                    _cancellationToken.ThrowIfCancellationRequested();

                    T result = await _work(_cancellationToken).ConfigureAwait(false);
                    _completion.TrySetResult(result);
                }
                catch (OperationCanceledException ex)
                {
                    _completion.TrySetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    // 普通任务失败只影响自己的票据，不退出消费循环。
                    _completion.TrySetException(ex);
                }
            }
        }

        private readonly object _gate = new object();
        private readonly object _owner = new object();
        private readonly Queue<IWorkItem> _queue = new Queue<IWorkItem>();
        private readonly SemaphoreSlim _ready = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _slots;
        private readonly Task[] _workers;
        private readonly Task _completion;
        private readonly Action _verifyBusinessThread;
        private bool _closed;

        public int WorkerCount { get { return _workers.Length; } }
        public int MaxConcurrency { get; private set; }
        public ResultWindow Results { get; private set; }

        /// <summary>
        /// 所有消费者都退出、共享资源释放后的生命周期任务。
        /// 不代表某张票据或某个业务入口的执行结果。
        /// </summary>
        public Task Completion { get { return _completion; } }

        /// <param name="workerCount">消费者数量 M，每个消费者一次执行一条任务。</param>
        /// <param name="maxConcurrency">共享并发上限 K，实际执行数不超过 min(M, K)。</param>
        /// <param name="verifyBusinessThread">
        /// 可选调用入口校验。Unity 示例注入主线程检查，用于 Submit 和结果窗口。
        /// 该检查不切换线程，也不影响工作单在后台执行。
        /// </param>
        public TicketScheduler(
            int workerCount,
            int maxConcurrency,
            Action verifyBusinessThread = null)
        {
            if (workerCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(workerCount));
            if (maxConcurrency <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

            _verifyBusinessThread = verifyBusinessThread;
            MaxConcurrency = maxConcurrency;
            _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            Results = new ResultWindow(_owner, verifyBusinessThread);

            _workers = new Task[workerCount];
            for (int i = 0; i < _workers.Length; i++)
                _workers[i] = Task.Run(ConsumeAsync);

            // 从创建起观察全部 worker，只有全部退出后才释放共享信号量。
            _completion = FinishAsync();
        }

        public TaskTicket<T> Submit<T>(Func<Task<T>> work)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));

            return Submit<T>(_ => work(), CancellationToken.None);
        }

        public TaskTicket<T> Submit<T>(
            Func<CancellationToken, Task<T>> work,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_verifyBusinessThread != null)
                _verifyBusinessThread();
            if (work == null)
                throw new ArgumentNullException(nameof(work));

            var item = new WorkItem<T>(_owner, work, cancellationToken);

            lock (_gate)
            {
                if (_closed)
                    throw new InvalidOperationException("队列已关闭，不再接收新任务。");

                _queue.Enqueue(item);
                // 与关闭使用同一把锁；不会在 FinishAsync 释放后再 Release。
                _ready.Release();
            }

            return item.Ticket;
        }

        private async Task ConsumeAsync()
        {
            while (true)
            {
                await _ready.WaitAsync().ConfigureAwait(false);
                await _slots.WaitAsync().ConfigureAwait(false);

                // 一旦取得执行名额，所有分支都必须释放它。
                try
                {
                    IWorkItem item;
                    lock (_gate)
                    {
                        if (_queue.Count == 0)
                        {
                            if (_closed)
                                return;
                            continue;
                        }

                        item = _queue.Dequeue();
                    }

                    // 完整等待当前工作单，不能启动后就立即领取下一条。
                    await item.ExecuteAsync().ConfigureAwait(false);
                }
                finally
                {
                    _slots.Release();
                }
            }
        }

        /// <summary>
        /// 停止接收，排空已接收任务，再等待全部消费者退出。
        /// 可以重复调用。只由服务拥有者在整体退出时调用。
        /// </summary>
        public Task CompleteAsync()
        {
            lock (_gate)
            {
                if (!_closed)
                {
                    _closed = true;
                    // 每个 worker 最终都需要一次空队列唤醒才能退出。
                    _ready.Release(_workers.Length);
                }
            }

            return _completion;
        }

        private async Task FinishAsync()
        {
            try
            {
                await Task.WhenAll(_workers).ConfigureAwait(false);
            }
            finally
            {
                // 不放在某个 worker 内 Dispose；其他 worker 可能仍在等待。
                _ready.Dispose();
                _slots.Dispose();
            }
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(CompleteAsync());
        }
    }
}
