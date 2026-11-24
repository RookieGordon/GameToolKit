/*
 * description  : 资源状态中心（单一监视器 Monitor Object）。
 *                每个资源键至多处于 _loading（加载中）或 _ready（可用）两张表之一，互斥由表结构本身保证。
 *                所有状态提交都在 Gate 锁内完成；外部回调、引擎调用与等待全部在锁外进行。
 *                摘除即所有权：从 _ready 摘除取得唯一释放权，从等待列表摘除取得唯一结算权。
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    internal sealed class ResourceStore
    {
        internal readonly object Gate = new object();
        private readonly Dictionary<ResourceSource, SourceLane> _lanes = new Dictionary<ResourceSource, SourceLane>();
        private readonly Dictionary<ResourceKey, LoadOperation> _loading = new Dictionary<ResourceKey, LoadOperation>();
        private readonly Dictionary<ResourceKey, ResourceEntry> _ready = new Dictionary<ResourceKey, ResourceEntry>();
        private readonly List<ResourceResidue> _residues = new List<ResourceResidue>();
        private readonly IExecutionContext _context;
        private readonly Action<Exception> _diagnose;
        private bool _stopped;

        public ResourceStore(IExecutionContext context, Action<Exception> diagnose)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _diagnose = diagnose ?? throw new ArgumentNullException(nameof(diagnose));
        }

        internal bool IsStopped => _stopped;

        /// <summary> 注册或替换一个来源的执行通道；已在途的操作照常完成，新请求与后续新尝试走新通道 </summary>
        public void SetLane(ResourceSource source, SourceLane lane)
        {
            lock (Gate)
            {
                EnsureOpenInLock();
                _lanes[source] = lane;
            }
        }

        internal SourceLane? TryGetLaneInLock(ResourceSource source)
        {
            _lanes.TryGetValue(source, out var lane);
            return lane;
        }

        /// <summary> 取得一份持有：命中可用表直接计持有返回；否则加入或启动该键的唯一加载操作 </summary>
        public async Task<ResourceEntry> AcquireAsync(ResourceKey key, IProgress<OperationProgress>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (TryAcquireReady(key, out var cached, out var lane))
                return cached!;

            using (var budget = lane.Policy.LoadTimeout.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(token) : null)
            {
                var effectiveToken = token;
                if (budget != null)
                {
                    budget.CancelAfter(lane.Policy.LoadTimeout!.Value);
                    effectiveToken = budget.Token;
                }

                if (TryJoinOrAcquireReady(key, progress, out var hit, out var operation, out var request, out var replay, out lane, out var start))
                    return hit!;

                if (start) _ = operation!.RunAsync(lane);
                if (replay != null && progress != null) operation!.ReportIsolated(progress, replay);

                try
                {
                    return await AwaitRequestAsync(operation!, request!, effectiveToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (budget != null && budget.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    throw ResourceException.Of(DiagnosticCodes.RequestTimeout, LoadStage.WaitForLoad, key);
                }
            }
        }

        /// <summary> 对仍在可用表中的条目追加一份持有；条目已被卸载摘除时返回 false </summary>
        public bool TryRetain(ResourceEntry entry)
        {
            lock (Gate)
            {
                if (!_ready.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
                    return false;
                entry.Holds++;
                return true;
            }
        }

        /// <summary> 归还一份持有；任意线程，不阻塞 </summary>
        public void Release(ResourceEntry entry)
        {
            lock (Gate)
            {
                if (entry.Holds > 0) entry.Holds--;
            }
        }

        /// <summary> 卸载选中且当前无人持有的可用产物，并重试选中的残留；锁内摘除取得释放权，锁外在上下文内释放 </summary>
        public async Task<UnloadResult> UnloadAsync(Func<ResourceKey, bool>? selector = null)
        {
            var result = new UnloadResult();
            var taken = TakeUnloadTargets(selector, result.SkippedInUse);

            foreach (var entry in taken)
            {
                try
                {
                    await _context.RunAsync(entry.Product.Release).ConfigureAwait(false);
                    result.Released.Add(entry.Key);
                }
                catch (Exception error)
                {
                    var residue = new ResourceResidue(entry, error);
                    lock (Gate) _residues.Add(residue);
                    result.Failed.Add(residue);
                }
            }
            return result;
        }

        /// <summary> 停止接收新请求，取消全部在途加载操作并等待后端与收尾清理真正退出 </summary>
        public Task StopAsync()
        {
            LoadOperation[] operations;
            var canceledWaiters = new List<LoadOperation.Request>();
            var canceledAttempts = new List<CancellationTokenSource>();
            lock (Gate)
            {
                _stopped = true;
                operations = new LoadOperation[_loading.Count];
                _loading.Values.CopyTo(operations, 0);
                foreach (var operation in operations)
                    operation.AbortInLock(canceledWaiters, canceledAttempts);
            }
            foreach (var cts in canceledAttempts) cts.Cancel();
            foreach (var waiter in canceledWaiters) waiter.Completion.TrySetCanceled();
            return operations.Length == 0
                ? Task.CompletedTask
                : Task.WhenAll(Array.ConvertAll(operations, op => op.Completion));
        }

        public void KeepResidue(ResourceKey key, IResource product, Exception error)
        {
            var entry = new ResourceEntry(key, product, 0);
            lock (Gate) _residues.Add(new ResourceResidue(entry, error));
        }

        public void Snapshot(List<ResourceEntry>? entries, List<ResourceResidue>? residues)
        {
            lock (Gate)
            {
                entries?.AddRange(_ready.Values);
                residues?.AddRange(_residues);
            }
        }

        internal ResourceEntry PublishReadyInLock(ResourceKey key, IResource product, int holds)
        {
            _loading.Remove(key);
            var entry = new ResourceEntry(key, product, holds);
            _ready[key] = entry;
            return entry;
        }

        internal void RemoveLoadingInLock(ResourceKey key, LoadOperation operation)
        {
            if (_loading.TryGetValue(key, out var current) && ReferenceEquals(current, operation))
                _loading.Remove(key);
        }

        private bool TryAcquireReady(ResourceKey key, out ResourceEntry? hit, out SourceLane lane)
        {
            lock (Gate)
            {
                return TryAcquireReadyOrGetLaneInLock(key, out hit, out lane);
            }
        }

        private bool TryJoinOrAcquireReady(
            ResourceKey key,
            IProgress<OperationProgress>? progress,
            out ResourceEntry? hit,
            out LoadOperation? operation,
            out LoadOperation.Request? request,
            out OperationProgress? replay,
            out SourceLane lane,
            out bool start)
        {
            lock (Gate)
            {
                if (TryAcquireReadyOrGetLaneInLock(key, out hit, out lane))
                {
                    operation = null;
                    request = null;
                    replay = null;
                    start = false;
                    return true;
                }

                start = !_loading.TryGetValue(key, out operation);
                if (start)
                {
                    operation = new LoadOperation(key, this, _diagnose);
                    _loading[key] = operation;
                }
                request = operation!.JoinInLock(progress, out replay);
                return false;
            }
        }

        private bool TryAcquireReadyOrGetLaneInLock(ResourceKey key, out ResourceEntry? hit, out SourceLane lane)
        {
            EnsureOpenInLock();
            if (_ready.TryGetValue(key, out hit))
            {
                hit.Holds++;
                lane = null!;
                return true;
            }
            if (!_lanes.TryGetValue(key.Source, out lane!))
                throw ResourceException.Of(DiagnosticCodes.LoaderNotRegistered, LoadStage.Route, key);
            return false;
        }

        private List<ResourceEntry> TakeUnloadTargets(Func<ResourceKey, bool>? selector, List<ResourceKey> skippedInUse)
        {
            var taken = new List<ResourceEntry>();
            lock (Gate)
            {
                foreach (var entry in _ready.Values)
                {
                    if (selector != null && !selector(entry.Key)) continue;
                    if (entry.Holds > 0) { skippedInUse.Add(entry.Key); continue; }
                    taken.Add(entry);
                }
                foreach (var entry in taken) _ready.Remove(entry.Key);

                for (var i = _residues.Count - 1; i >= 0; i--)
                {
                    if (selector != null && !selector(_residues[i].Key)) continue;
                    taken.Add(_residues[i].Entry);
                    _residues.RemoveAt(i);
                }
            }
            return taken;
        }

        private static async Task<ResourceEntry> AwaitRequestAsync(
            LoadOperation operation,
            LoadOperation.Request request,
            CancellationToken effectiveToken)
        {
            if (request.Completion.Task.IsCompleted || !effectiveToken.CanBeCanceled)
                return await request.Completion.Task.ConfigureAwait(false);

            using (effectiveToken.Register(static state =>
            {
                var item = ((LoadOperation Op, LoadOperation.Request Req, CancellationToken Ct))state!;
                item.Op.CancelRequest(item.Req, item.Ct);
            }, (operation, request, effectiveToken)))
            {
                return await request.Completion.Task.ConfigureAwait(false);
            }
        }

        private void EnsureOpenInLock()
        {
            if (_stopped)
                throw new ResourceException(new ResourceError(DiagnosticCodes.LifecycleSystemDisposed, LoadStage.ValidateRequest));
        }
    }
}
