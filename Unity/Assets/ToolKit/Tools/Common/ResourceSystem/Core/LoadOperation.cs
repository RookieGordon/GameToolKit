/*
 * description  : 单个资源键的在途加载操作（主动对象 Active Object）。
 *                状态与 ResourceStore 共用同一把锁，遵守"锁内决定、锁外执行、摘除即所有权"：
 *                1. 有序维护等待请求，每轮尝试开始时选最早等待者为承担者 (需求 Q01)；
 *                2. 承担者提前取消只结束本人，失败不转嫁他人；剩余等待者在同一驱动循环内进入下一轮；
 *                3. 成功时在同一临界区内登记产物、计入全部等待者持有并摘除等待者，杜绝交付空档；
 *                4. 最后一个等待者离开时取消当前尝试，迟到产物在释放并发名额前直接清理。
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    internal sealed class LoadOperation
    {
        internal sealed class Request
        {
            public readonly TaskCompletionSource<ResourceEntry> Completion =
                new TaskCompletionSource<ResourceEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly IProgress<OperationProgress>? Progress;
            public LinkedListNode<Request>? Node;

            public Request(IProgress<OperationProgress>? progress) => Progress = progress;
        }

        private sealed class Attempt : IProgress<OperationProgress>
        {
            private readonly LoadOperation _owner;
            private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
            private ResourceEntry? _committed;
            private Request[]? _receivers;

            public Attempt(LoadOperation owner, Request bearer)
            {
                _owner = owner;
                Bearer = bearer;
            }

            public Request Bearer { get; }
            public CancellationToken Token => _cancellation.Token;
            public bool Abandoned { get; private set; }

            public CancellationTokenSource? Abandon()
            {
                if (Abandoned) return null;
                Abandoned = true;
                return _cancellation;
            }

            public bool TryCommit(IResource product)
            {
                lock (_owner._store.Gate)
                {
                    if (Abandoned || _owner._waiters.First == null || _owner._store.IsStopped)
                        return false;

                    _owner._attempt = null;
                    _receivers = _owner.DetachAllWaitersInLock();
                    _committed = _owner._store.PublishReadyInLock(_owner.Key, product, _receivers.Length);
                    return true;
                }
            }

            public bool DeliverIfCommitted()
            {
                if (_committed == null || _receivers == null) return false;
                foreach (var receiver in _receivers)
                    receiver.Completion.TrySetResult(_committed);
                return true;
            }

            public void Report(OperationProgress value)
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                _owner.BroadcastProgress(this, value);
            }
        }

        private readonly ResourceStore _store;
        private readonly Action<Exception> _diagnose;
        private readonly LinkedList<Request> _waiters = new LinkedList<Request>();
        private readonly TaskCompletionSource<bool> _done =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Attempt? _attempt;
        private OperationProgress? _latestProgress;

        public LoadOperation(ResourceKey key, ResourceStore store, Action<Exception> diagnose)
        {
            Key = key;
            _store = store;
            _diagnose = diagnose;
        }

        public ResourceKey Key { get; }
        public Task Completion => _done.Task;

        public Request JoinInLock(IProgress<OperationProgress>? progress, out OperationProgress? replay)
        {
            var request = new Request(progress);
            request.Node = _waiters.AddLast(request);
            replay = progress != null ? _latestProgress : null;
            return request;
        }

        public void CancelRequest(Request request, CancellationToken token)
        {
            CancellationTokenSource? attemptToCancel = null;
            lock (_store.Gate)
            {
                if (request.Node?.List == null) return;
                _waiters.Remove(request.Node);
                if (_waiters.Count == 0 && _attempt != null)
                    attemptToCancel = _attempt.Abandon();
            }
            request.Completion.TrySetCanceled(token);
            attemptToCancel?.Cancel();
        }

        public void AbortInLock(List<Request> canceledWaiters, List<CancellationTokenSource> canceledAttempts)
        {
            while (_waiters.First != null)
            {
                var waiter = _waiters.First.Value;
                _waiters.RemoveFirst();
                canceledWaiters.Add(waiter);
            }
            var cts = _attempt?.Abandon();
            if (cts != null) canceledAttempts.Add(cts);
        }

        public async Task RunAsync(SourceLane initialLane)
        {
            try
            {
                var lane = initialLane;
                while (true)
                {
                    var attempt = StartAttemptInLock(ref lane);
                    if (attempt == null) return;

                    Exception? failure = null;
                    try
                    {
                        await lane.ProduceAsync(Key, attempt, attempt.Token, attempt.TryCommit).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested) { }
                    catch (Exception error) { failure = error; }

                    if (attempt.DeliverIfCommitted()) return;

                    var finished = EndAttemptInLock(attempt, failure, out var failedBearer);
                    if (failedBearer != null && failure != null)
                        failedBearer.Completion.TrySetException(failure);
                    if (finished) return;
                }
            }
            finally
            {
                _done.TrySetResult(true);
            }
        }

        public void ReportIsolated(IProgress<OperationProgress> listener, OperationProgress value)
        {
            try { listener.Report(value); }
            catch (Exception error)
            {
                try { _diagnose(error); } catch { }
            }
        }

        private Attempt? StartAttemptInLock(ref SourceLane lane)
        {
            lock (_store.Gate)
            {
                if (_waiters.First == null || _store.IsStopped)
                {
                    _store.RemoveLoadingInLock(Key, this);
                    return null;
                }
                lane = _store.TryGetLaneInLock(Key.Source) ?? lane;
                _latestProgress = null;
                _attempt = new Attempt(this, _waiters.First.Value);
                return _attempt;
            }
        }

        private bool EndAttemptInLock(Attempt attempt, Exception? failure, out Request? failedBearer)
        {
            lock (_store.Gate)
            {
                _attempt = null;
                failedBearer = null;

                if (failure != null && !attempt.Abandoned && attempt.Bearer.Node?.List != null)
                {
                    _waiters.Remove(attempt.Bearer.Node);
                    failedBearer = attempt.Bearer;
                }

                var finished = _waiters.First == null || _store.IsStopped;
                if (finished) _store.RemoveLoadingInLock(Key, this);
                return finished;
            }
        }

        private Request[] DetachAllWaitersInLock()
        {
            var receivers = new Request[_waiters.Count];
            var index = 0;
            while (_waiters.First != null)
            {
                receivers[index++] = _waiters.First.Value;
                _waiters.RemoveFirst();
            }
            return receivers;
        }

        private void BroadcastProgress(Attempt source, OperationProgress value)
        {
            List<IProgress<OperationProgress>>? listeners = null;
            lock (_store.Gate)
            {
                if (_attempt != source || source.Abandoned) return;
                _latestProgress = value;
                for (var node = _waiters.First; node != null; node = node.Next)
                {
                    if (node.Value.Progress == null) continue;
                    listeners ??= new List<IProgress<OperationProgress>>(_waiters.Count);
                    listeners.Add(node.Value.Progress);
                }
            }
            if (listeners == null) return;
            foreach (var listener in listeners) ReportIsolated(listener, value);
        }
    }
}
