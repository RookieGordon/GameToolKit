/*
 * author       : Gordon
 * datetime     : 2026/10/10
 * description  : 已加载产物的仓库记录；不保存加载任务、请求或解析输入。
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 一份实际产物及其持有、缓存信息。由 ResourceStore 维护，ResourceRef 绑定这份具体记录。
    /// 加载成功才创建，底层释放完成后退出仓库；不会反向引用加载上下文。
    /// </summary>
    internal sealed class ResourceEntry
    {
        public readonly ResourceKey Key;
        public readonly LoadedAsset Asset;
        public readonly MemoryPolicy MemoryPolicy;
        public ResourceState State = ResourceState.Ready;
        public bool IsAssetInvalid;
        public int HoldCount;
        public readonly TaskCompletionSource<bool> RemovalCompletion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public double IdleSince;
        public long EstimatedBytes => Asset.EstimatedBytes ?? 0;
        public LoadError? CleanupError;
        public LinkedListNode<ResourceEntry>? IdleNode;

        public ResourceEntry(ResourceKey key, LoadedAsset asset, MemoryPolicy memoryPolicy)
        {
            Key = key;
            Asset = asset ?? throw new ArgumentNullException(nameof(asset));
            MemoryPolicy = memoryPolicy;
        }

        /// <summary> 成功表示旧条目已移除，可重新加载；失败表示旧条目清理失败，禁止重试。 </summary>
        public Task RemovalTask => RemovalCompletion.Task;
    }
}
