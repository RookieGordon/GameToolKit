using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 统一工作中心 = 共享操作（同 key 多等一）+ 工作队列（M 个消费者、K 个执行名额）。
    /// 本类只做组合：同 key 的请求先在 SharedOperation 归并成一次执行，这一次执行再作为一张票据进入 TicketScheduler。
    /// 工单、撤单、进度、回调线程、关闭语义全部沿用两个组件自己的定义，这里不再发明新概念。
    /// </summary>
    public sealed class WorkCenter<TKey, TResult>
    {
        private readonly TicketScheduler _scheduler;
        private readonly SharedOperation<TKey, TResult> _shared;

        /// <param name="work">真正的业务执行；收到的 token 是"这一轮执行"的取消，不是某个等待者的取消。</param>
        /// <param name="workerCount">消费者数量 M。</param>
        /// <param name="maxConcurrency">执行名额 K，实际并发不超过 min(M, K)。</param>
        /// <param name="policy">最后一个等待者退出时，后台继续还是取消执行。</param>
        /// <param name="comparer">key 比较器；默认按类型默认相等。</param>
        /// <param name="diagnose">无人承接的失败、回调或进度监听器抛出的异常走这里；默认 Trace.TraceError。</param>
        public WorkCenter(
            Func<TKey, IProgress<OperationProgress>, CancellationToken, Task<TResult>> work,
            int workerCount,
            int maxConcurrency,
            NoWaitersPolicy policy = NoWaitersPolicy.ContinueInBackground,
            IEqualityComparer<TKey> comparer = null,
            Action<Exception> diagnose = null)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));

            // Submit 由共享操作在任意线程上调用（首个请求者的线程或线程池），所以不注入业务线程校验。
            _scheduler = new TicketScheduler(workerCount, maxConcurrency);
            _shared = new SharedOperation<TKey, TResult>(
                (key, progress, token) => _scheduler.Results.WaitAsync(
                    _scheduler.Submit(ct => work(key, progress, ct), token)),
                policy, comparer, diagnose);
        }

        public int WorkerCount { get { return _scheduler.WorkerCount; } }
        public int MaxConcurrency { get { return _scheduler.MaxConcurrency; } }
        public NoWaitersPolicy Policy { get { return _shared.Policy; } }

        /// <summary>一次调用就是一张工单：返回的 Task 可多次 await；token 取消只结束自己的等待。</summary>
        public Task<TResult> GetAsync(
            TKey key,
            IProgress<OperationProgress> progress = null,
            CancellationToken token = default(CancellationToken))
        {
            return _shared.GetAsync(key, progress, token);
        }

        /// <summary>回调风格：结果与进度回到订阅时的 SynchronizationContext；Dispose 后不再有任何回调。</summary>
        public IDisposable Subscribe(
            TKey key,
            Action<OperationResult<TResult>> onResult,
            Action<OperationProgress> onProgress = null)
        {
            return _shared.Subscribe(key, onResult, onProgress);
        }

        /// <summary>拒绝新请求；在途与排队中的执行照常做完；然后消费者退出。</summary>
        public async Task CompleteAsync()
        {
            await _shared.CompleteAsync().ConfigureAwait(false);
            await _scheduler.CompleteAsync().ConfigureAwait(false);
        }

        /// <summary>拒绝新请求；取消在途与排队中的执行并等它们真正退出；然后消费者退出。</summary>
        public async Task StopAsync()
        {
            await _shared.StopAsync().ConfigureAwait(false);
            await _scheduler.CompleteAsync().ConfigureAwait(false);
        }
    }
}
