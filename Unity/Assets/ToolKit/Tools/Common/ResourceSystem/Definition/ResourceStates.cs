/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 资源系统状态枚举集合 (P0)。取值集合为第一版完整集合：
 *                取消与失败不是资源错误码，不进入这些状态机的"错误值"分支。
 */

namespace ToolKit.Tools.Common
{
    /// <summary> 管理器生命周期：首次请求使 Configuring 冻结为 Running </summary>
    public enum ManagerState
    {
        Configuring,
        Running,
        Closing,
        Closed,
        Faulted,
    }

    /// <summary>
    /// 资源条目状态机 (§5.2)：
    /// Loading → Ready(有等待者成功领取) / Draining(最后等待者取消) / Removed(失败回退完成)；
    /// Ready ↔ Idle(持有归零/再次领取)；Idle/Ready → Unloading；Unloading → Removed / ReleaseFailed。
    /// </summary>
    public enum ResourceState
    {
        /// <summary> 加载中，新请求可以一起等待这次结果。 </summary>
        Loading,
        /// <summary> 已放弃这次加载，等待后端结束并清理迟到结果；新请求不能加入。 </summary>
        Draining,
        /// <summary> 资源可用，至少有一份已发出的引用。 </summary>
        Ready,
        /// <summary> 没有引用，暂存于内存缓存，可直接复用。 </summary>
        Idle,
        /// <summary> 正在释放底层资源；结束前不能开始同键的新加载。 </summary>
        Unloading,
        /// <summary> 未能确认清理完成，保留故障并阻止同键重新加载。 </summary>
        ReleaseFailed,
        /// <summary> 已退出仓库，旧引用不能使它重新生效。 </summary>
        Removed,
    }

    /// <summary> 等待者状态：只允许转换一次 </summary>
    public enum WaiterState
    {
        Pending,
        Granted,
        Cancelled,
        Failed,
    }

    /// <summary> 池桶状态 (§5.4) </summary>
    public enum PoolState
    {
        Initializing,
        Open,
        Closing,
        Closed,
        Faulted,
    }

    /// <summary> 实例记录状态 (§7.1)；Preparing/Returning 保护回调期间的实例 </summary>
    public enum InstanceState
    {
        Preparing,
        Active,
        Returning,
        Idle,
        Destroying,
        Destroyed,
        CleanupFailed,
    }
}
