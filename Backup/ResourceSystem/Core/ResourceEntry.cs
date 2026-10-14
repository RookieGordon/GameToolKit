/*
 * author       : Gordon
 * datetime     : 2026/10/10
 * description  : 已加载产物的仓库记录；不保存加载任务、请求或解析输入。
 */

using System;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 一份具体产物及其持有状态。失效后仍保留，业务卸载结束后退出仓库。 </summary>
    internal sealed class ResourceEntry
    {
        public readonly ResourceKey Key;
        public readonly LoadedAsset Asset;
        public ResourceState State = ResourceState.Ready;
        public bool IsAssetInvalid;
        public int HoldCount;
        public readonly TaskCompletionSource<DirtyResource?> RemovalCompletion =
            new TaskCompletionSource<DirtyResource?>(TaskCreationOptions.RunContinuationsAsynchronously);

        public ResourceEntry(ResourceKey key, LoadedAsset asset)
        {
            Key = key;
            Asset = asset ?? throw new ArgumentNullException(nameof(asset));
        }

        /// <summary> 完成即退出正常仓库；非空结果表示残留对象已移交脏集合。 </summary>
        public Task<DirtyResource?> RemovalTask => RemovalCompletion.Task;
    }
}
