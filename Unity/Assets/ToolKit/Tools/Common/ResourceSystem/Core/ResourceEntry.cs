/*
 * author       : Gordon
 * datetime     : 2026/10/10
 * description  : 共享资源条目及等待领取的请求。排队由 KeyedAsyncLock 负责，条目不逐个发送完成通知。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 尚未领取引用的请求；保留进度订阅，并在领取之前保护加载结果。 </summary>
    internal sealed class PendingResourceRequest
    {
        public readonly IProgress<ResourceProgress>? Progress;
        public readonly CancellationToken CallerToken;

        public PendingResourceRequest(IProgress<ResourceProgress>? progress, CancellationToken callerToken)
        {
            Progress = progress;
            CallerToken = callerToken;
        }
    }

    /// <summary> 同一资源的一轮加载及后续持有。最后一次释放完成后，这个条目退出仓库。 </summary>
    internal sealed class ResourceEntry
    {
        public readonly ResourceKey Key;
        public readonly LoaderRegistration Registration;
        public readonly ResolvedResource Resolved;
        public readonly string OperationId;
        public readonly MemoryPolicy Policy;
        public ResourceState State;
        public LoadedAsset? Asset;
        public bool IsAssetInvalid;
        public int HoldCount;
        public bool HasBeenReferenced;
        public readonly List<PendingResourceRequest> PendingRequests = new List<PendingResourceRequest>();
        // 执行中的后端操作拥有资源锁，调用者取消等待不会提前释放它。
        public Task? LoadTask;
        public LoadError? LoadError;
        public CancellationTokenSource? LoadCancellation;
        public readonly TaskCompletionSource<bool> RemovalCompletion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public double IdleSince;
        public long EstimatedBytes;
        public LoadError? CleanupError;
        public LinkedListNode<ResourceEntry>? IdleNode;

        public ResourceEntry(ResourceKey key, LoaderRegistration registration, ResolvedResource resolved,
            string operationId, MemoryPolicy policy)
        {
            Key = key;
            Registration = registration;
            Resolved = resolved;
            OperationId = operationId;
            Policy = policy;
            State = ResourceState.Loading;
        }

        public CancellationToken LoadToken => LoadCancellation?.Token ?? CancellationToken.None;

        /// <summary> 成功表示旧条目已移除，可重新加载；失败表示旧条目清理失败，禁止重试。 </summary>
        public Task RemovalTask => RemovalCompletion.Task;
    }
}
