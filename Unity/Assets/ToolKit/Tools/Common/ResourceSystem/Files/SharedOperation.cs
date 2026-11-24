/*
 * description  : 值结果共享原语：按 key 合并在途执行，适用于结果无独立所有权的值操作（如文件填充）。
 *                个人取消只结束自己的等待；最后一个等待者退出时取消当轮执行；
 *                新请求等旧轮真正退出后再开新一轮，保证同一 key 执行不重叠。
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public sealed class SharedOperation<TKey, TResult> where TKey : notnull
    {
        private readonly object _gate = new object();
        private readonly Dictionary<TKey, Entry> _inFlight;
        private readonly Func<TKey, IProgress<OperationProgress>, CancellationToken, Task<TResult>> _execute;
        private readonly Action<Exception> _diagnose;
        private bool _stopped;

        public SharedOperation(
            Func<TKey, IProgress<OperationProgress>, CancellationToken, Task<TResult>> execute,
            IEqualityComparer<TKey>? comparer = null,
            Action<Exception>? diagnose = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _inFlight = new Dictionary<TKey, Entry>(comparer);
            _diagnose = diagnose ?? (error => Trace.TraceError(error.ToString()));
        }

        /// <summary>
        /// 同 key 在途则加入，否则启动一轮。token 取消只结束本次请求；最后一个等待者退出时取消本轮执行。
        /// 晚加入者先收到本轮最新一次进度回放，再接收后续进度。
        /// </summary>
        public async Task<TResult> GetAsync(
            TKey key,
            IProgress<OperationProgress>? progress = null,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Entry? entry;
            Entry? previous = null;
            OperationProgress? replay = null;
            var start = false;

            lock (_gate)
            {
                if (_stopped) throw new InvalidOperationException("服务已停止。");
                if (!_inFlight.TryGetValue(key, out entry) || !entry.Joinable)
                {
                    previous = entry;
                    entry = new Entry();
                    _inFlight[key] = entry;
                    start = true;
                }
                entry.Waiters++;
                if (progress != null)
                {
                    entry.Listeners.Add(progress);
                    replay = entry.Latest;
                }
            }

            try
            {
                if (replay != null && progress != null) ReportProgress(progress, replay);
                if (start) _ = RunAsync(key, entry, previous);
                return await WaitAsync(entry.Completion.Task, token).ConfigureAwait(false);
            }
            finally
            {
                Leave(entry, progress);
            }
        }

        /// <summary> 拒绝新请求，取消所有在途执行，并等待它们真正退出。可重复调用。 </summary>
        public Task StopAsync()
        {
            Entry[] entries;
            lock (_gate)
            {
                _stopped = true;
                entries = _inFlight.Values.ToArray();
                foreach (var entry in entries) entry.Joinable = false;
            }
            foreach (var entry in entries) entry.Cancellation.Cancel();
            return entries.Length == 0
                ? Task.CompletedTask
                : Task.WhenAll(entries.Select(entry => Quiet(entry.Completion.Task)));
        }

        private async Task RunAsync(TKey key, Entry entry, Entry? previous)
        {
            TResult value = default!;
            Exception? error = null;
            try
            {
                if (previous != null) await Quiet(previous.Completion.Task).ConfigureAwait(false);
                entry.Cancellation.Token.ThrowIfCancellationRequested();
                value = await _execute(key, new Reporter(this, entry), entry.Cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                error = e;
            }

            bool abandoned;
            lock (_gate)
            {
                entry.Joinable = false;
                if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    _inFlight.Remove(key);
                abandoned = entry.Waiters == 0;
            }

            if (error == null) entry.Completion.SetResult(value);
            else if (error is OperationCanceledException canceled) entry.Completion.TrySetCanceled(canceled.CancellationToken);
            else
            {
                entry.Completion.SetException(error);
                if (abandoned) Diagnose(error);
            }
        }

        private void Leave(Entry entry, IProgress<OperationProgress>? progress)
        {
            bool cancel;
            lock (_gate)
            {
                if (progress != null) entry.Listeners.Remove(progress);
                entry.Waiters--;
                cancel = entry.Waiters == 0 && entry.Joinable;
                if (cancel) entry.Joinable = false;
            }
            if (cancel) entry.Cancellation.Cancel();
        }

        private void ReportProgress(IProgress<OperationProgress> progress, OperationProgress value)
        {
            try { progress.Report(value); }
            catch (Exception error) { Diagnose(error); }
        }

        private void Diagnose(Exception error)
        {
            try { _diagnose(error); }
            catch { }
        }

        private static async Task<TResult> WaitAsync(Task<TResult> task, CancellationToken token)
        {
            if (task.IsCompleted || !token.CanBeCanceled) return await task.ConfigureAwait(false);
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), canceled))
            {
                if (await Task.WhenAny(task, canceled.Task).ConfigureAwait(false) != task)
                    throw new OperationCanceledException(token);
            }
            return await task.ConfigureAwait(false);
        }

        private static Task Quiet(Task task)
        {
            return task.ContinueWith(static _ => { }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private sealed class Entry
        {
            public readonly TaskCompletionSource<TResult> Completion =
                new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public readonly List<IProgress<OperationProgress>> Listeners = new List<IProgress<OperationProgress>>();
            public OperationProgress? Latest;
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
                foreach (var listener in listeners) _owner.ReportProgress(listener, value);
            }
        }
    }
}
