using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary>最后一个等待者退出后，本轮执行怎么办。</summary>
    public enum NoWaitersPolicy
    {
        /// <summary>照常跑完。之后来的同 key 请求仍加入这一轮。</summary>
        ContinueInBackground,

        /// <summary>请求取消。本轮作废，之后来的同 key 请求等它真正退出后开新一轮。</summary>
        CancelExecution
    }

    /// <summary>
    /// 按 key 合并在途执行：同 key 在途就加入，否则启动一轮。
    /// 一次 GetAsync 就是一张工单：返回的 Task 是结果，传入的 token 是撤单，
    /// 回到调用方线程靠 await 自己的 SynchronizationContext，不需要分发器。
    /// </summary>
    public sealed class SharedOperation<TKey, TResult>
    {
        private readonly object _gate = new object();
        private readonly Dictionary<TKey, Entry> _inFlight;
        private readonly Func<TKey, IProgress<OperationProgress>, CancellationToken, Task<TResult>> _execute;
        private readonly NoWaitersPolicy _policy;
        private readonly Action<Exception> _diagnose;
        private bool _stopped;

        /// <param name="execute">
        /// 真正干活的函数。首个 await 之前的同步部分在发起请求的线程上运行；
        /// 需要后台执行时自行 Task.Run，或者像 UnifiedWorkDemo_v3 那样交给工作队列。
        /// </param>
        /// <param name="policy">最后一个等待者退出后的处理方式。</param>
        /// <param name="comparer">key 的相等比较器，默认按 TKey 的默认相等性。</param>
        /// <param name="diagnose">无人承接的失败、回调抛出的异常走这里，默认 Trace.TraceError。</param>
        public SharedOperation(
            Func<TKey, IProgress<OperationProgress>, CancellationToken, Task<TResult>> execute,
            NoWaitersPolicy policy = NoWaitersPolicy.ContinueInBackground,
            IEqualityComparer<TKey> comparer = null,
            Action<Exception> diagnose = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _policy = policy;
            _inFlight = new Dictionary<TKey, Entry>(comparer);
            _diagnose = diagnose ?? (error => Trace.TraceError(error.ToString()));
        }

        public NoWaitersPolicy Policy { get { return _policy; } }

        /// <summary>
        /// 同 key 在途则加入，否则启动一轮。返回的 Task 可以多次 await。
        /// token 取消只结束本次请求；最后一个请求退出时按策略处理执行。
        /// progress 可选；晚加入者先收到本轮最新一次进度，再收到后续进度。
        /// </summary>
        public async Task<TResult> GetAsync(TKey key, IProgress<OperationProgress> progress = null,
            CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            Entry entry, previous = null;
            OperationProgress replay = null;
            bool start = false;
            lock (_gate)
            {
                if (_stopped) throw new InvalidOperationException("服务已停止。");
                if (!_inFlight.TryGetValue(key, out entry) || !entry.Joinable)
                {
                    previous = entry;                       // 正在取消退出的旧轮；没有则为 null
                    entry = new Entry();
                    _inFlight[key] = entry;
                    start = true;
                }
                entry.Waiters++;
                if (progress != null)
                {
                    entry.Listeners.Add(progress);
                    replay = entry.Latest;                  // 晚加入者回放最新进度
                }
            }
            if (replay != null) progress.Report(replay);
            if (start) Forget(RunAsync(key, entry, previous));
            try { return await WaitAsync(entry.Completion.Task, token).ConfigureAwait(false); }
            finally { Leave(entry, progress); }
        }

        /// <summary>
        /// 回调形式。结果和进度回调都回到调用 Subscribe 时的 SynchronizationContext（没有则线程池）。
        /// Dispose 退订：之后不再有任何回调；作为最后一个退出者时按策略处理执行。
        /// </summary>
        public IDisposable Subscribe(TKey key, Action<OperationResult<TResult>> onResult,
            Action<OperationProgress> onProgress = null)
        {
            if (onResult == null) throw new ArgumentNullException(nameof(onResult));
            var cts = new CancellationTokenSource();
            var progress = onProgress == null ? null : new Progress<OperationProgress>(onProgress);
            Forget(ObserveAsync(key, progress, cts.Token, onResult));
            return new Subscription(cts);
        }

        /// <summary>拒绝新请求；在途执行照常跑完，等待它们全部结束。可重复调用。</summary>
        public Task CompleteAsync()
        {
            return ShutdownAsync(false);
        }

        /// <summary>拒绝新请求，取消所有在途执行，等待它们真正退出。可重复调用。</summary>
        public Task StopAsync()
        {
            return ShutdownAsync(true);
        }

        private Task ShutdownAsync(bool cancel)
        {
            Entry[] entries;
            lock (_gate)
            {
                _stopped = true;
                entries = _inFlight.Values.ToArray();
                if (cancel) foreach (Entry entry in entries) entry.Joinable = false;
            }
            if (cancel) foreach (Entry entry in entries) entry.Cancellation.Cancel();
            // 被交接的旧轮已不在字典里，但新一轮要等它退出才会结束，所以这里仍然等到了它。
            return Task.WhenAll(entries.Select(entry => Quiet(entry.Completion.Task)));
        }

        private async Task RunAsync(TKey key, Entry entry, Entry previous)
        {
            TResult value = default(TResult);
            Exception error = null;
            try
            {
                // 同 key 不重叠：等旧轮真正退出再开始。不需要这条保证时删掉这一行。
                if (previous != null) await Quiet(previous.Completion.Task).ConfigureAwait(false);
                entry.Cancellation.Token.ThrowIfCancellationRequested();
                value = await _execute(key, new Reporter(this, entry), entry.Cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception e) { error = e; }

            bool abandoned;
            lock (_gate)
            {
                entry.Joinable = false;                     // 先封存再发布：之后的同 key 请求开新一轮
                Entry current;
                if (_inFlight.TryGetValue(key, out current) && current == entry) _inFlight.Remove(key);
                abandoned = entry.Waiters == 0;
            }
            if (error == null) entry.Completion.SetResult(value);
            else if (error is OperationCanceledException canceled) entry.Completion.TrySetCanceled(canceled.CancellationToken);
            else
            {
                entry.Completion.SetException(error);
                if (abandoned) Diagnose(error);             // 无人承接的失败走诊断
            }
        }

        private void Leave(Entry entry, IProgress<OperationProgress> progress)
        {
            bool cancel;
            lock (_gate)
            {
                if (progress != null) entry.Listeners.Remove(progress);
                entry.Waiters--;
                cancel = entry.Waiters == 0 && entry.Joinable && _policy == NoWaitersPolicy.CancelExecution;
                if (cancel) entry.Joinable = false;         // 作废本轮；后来者等它退出后新开一轮
            }
            if (cancel) entry.Cancellation.Cancel();
        }

        private async Task ObserveAsync(TKey key, IProgress<OperationProgress> progress,
            CancellationToken token, Action<OperationResult<TResult>> onResult)
        {
            OperationResult<TResult> result;
            try { result = OperationResult<TResult>.Success(await GetAsync(key, progress, token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (OperationCanceledException e) { result = OperationResult<TResult>.Canceled(e); }
            catch (Exception e) { result = OperationResult<TResult>.Failure(e); }
            if (token.IsCancellationRequested) return;      // 结果已到但页面刚退订：静默
            try { onResult(result); } catch (Exception e) { Diagnose(e); }
        }

        private void Diagnose(Exception error)
        {
            try { _diagnose(error); } catch { }
        }

        private static void Forget(Task task)
        {
            // RunAsync / ObserveAsync 自己兜住全部异常；这里只是明确表示不等待它们。
        }

        private static async Task<TResult> WaitAsync(Task<TResult> task, CancellationToken token)
        {
            if (task.IsCompleted || !token.CanBeCanceled) return await task.ConfigureAwait(false);
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(state => ((TaskCompletionSource<bool>)state).TrySetResult(true), canceled))
            {
                if (await Task.WhenAny(task, canceled.Task).ConfigureAwait(false) != task)
                    throw new OperationCanceledException(token);
            }
            return await task.ConfigureAwait(false);
        }

        private static Task Quiet(Task task)
        {
            return task.ContinueWith(_ => { }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private sealed class Entry
        {
            public readonly TaskCompletionSource<TResult> Completion =
                new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 不 Dispose：没有计时器的 CTS 不持有非托管资源，省掉和 Cancel 的竞争。
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public readonly List<IProgress<OperationProgress>> Listeners = new List<IProgress<OperationProgress>>();
            public OperationProgress Latest;
            public int Waiters;
            public bool Joinable = true;
        }

        private sealed class Reporter : IProgress<OperationProgress>
        {
            private readonly SharedOperation<TKey, TResult> _owner;
            private readonly Entry _entry;

            public Reporter(SharedOperation<TKey, TResult> owner, Entry entry)
            {
                _owner = owner;
                _entry = entry;
            }

            public void Report(OperationProgress value)
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                IProgress<OperationProgress>[] listeners;
                lock (_owner._gate)
                {
                    _entry.Latest = value;
                    listeners = _entry.Listeners.ToArray();
                }
                foreach (var listener in listeners)
                {
                    // Progress<T> 各自回到自己的上下文；某个监听器抛异常不连累执行和其他人。
                    try { listener.Report(value); } catch (Exception e) { _owner.Diagnose(e); }
                }
            }
        }

        private sealed class Subscription : IDisposable
        {
            private CancellationTokenSource _cts;

            public Subscription(CancellationTokenSource cts)
            {
                _cts = cts;
            }

            public void Dispose()
            {
                var cts = Interlocked.Exchange(ref _cts, null);
                if (cts != null) cts.Cancel();
            }
        }
    }
}
