/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 请求与配置快照 (P0)。默认值见 §9.1：
 *                0 表示"不保留/立即卸载"，不再沿用旧 ObjectPool 中"<=0 代表无限"的隐含约定；
 *                null 表示无上限；负容量或负时间在验证时直接拒绝。
 */

using System;
using System.Threading;

namespace ToolKit.Tools.Common
{
    /// <summary> 单次请求的可选覆盖：超时、进度、加载器专属参数。影响结果的参数由加载器纳入身份 </summary>
    public sealed class RequestOptions
    {
        /// <summary> 整个调用 (含 Resolve 与排队等待) 的时间预算；null 表示使用系统默认 </summary>
        public TimeSpan? Timeout { get; set; }

        public IProgress<ResourceProgress>? Progress { get; set; }

        /// <summary> 加载器专属、调用期间不可变的参数；核心不读取其内容 </summary>
        public object? Parameters { get; set; }
    }

    /// <summary> 系统级配置；合并后的策略在初始化时冻结为不可变快照 </summary>
    public sealed class ResourceSystemOptions
    {
        public string DefaultLoader { get; set; } = "";
        public string DefaultFactory { get; set; } = "gameObject";
        public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan? RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public MemoryPolicy Memory { get; set; } = new MemoryPolicy();
        public PoolPolicy Pool { get; set; } = new PoolPolicy();

        public void Validate()
        {
            if (MaintenanceInterval < TimeSpan.Zero)
            {
                throw new ArgumentException("MaintenanceInterval 不能为负");
            }
            if (RequestTimeout is TimeSpan t && t <= TimeSpan.Zero)
            {
                throw new ArgumentException("RequestTimeout 必须为正或 null");
            }
            Memory.Validate();
            Pool.Validate();
        }
    }

    /// <summary> 空闲内存资源策略：TTL + 条目数上限 + 可统计字节预算 (估算值，不宣称控制进程 RSS) </summary>
    public sealed class MemoryPolicy
    {
        /// <summary> 持有归零后保留时长；0 表示归零即卸载 </summary>
        public TimeSpan IdleLifetime { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary> 空闲条目上限；0 表示不保留空闲资源 </summary>
        public int MaxIdleEntries { get; set; } = 128;

        /// <summary> 空闲资源估算字节预算；null 表示不按字节限制 </summary>
        public long? MaxEstimatedIdleBytes { get; set; }

        public void Validate()
        {
            if (IdleLifetime < TimeSpan.Zero)
            {
                throw new ArgumentException("IdleLifetime 不能为负");
            }
            if (MaxIdleEntries < 0)
            {
                throw new ArgumentException("MaxIdleEntries 不能为负");
            }
            if (MaxEstimatedIdleBytes is long b && b < 0)
            {
                throw new ArgumentException("MaxEstimatedIdleBytes 不能为负");
            }
        }

        public static MemoryPolicy Default { get; } = new MemoryPolicy();
    }

    /// <summary> 具名加载器策略；未提供的字段采用系统默认的不可变快照 </summary>
    public sealed class LoaderPolicy
    {
        /// <summary> 按已解析不同资源的实际加载计数；小于等于 0 表示不限制 </summary>
        public int MaxConcurrentLoads { get; set; } = 4;

        public MemoryPolicy? Memory { get; set; }
    }

    /// <summary> 实例池策略 </summary>
    public sealed class PoolPolicy
    {
        /// <summary> 每桶闲置实例上限；0 表示归还即销毁 </summary>
        public int MaxIdlePerResource { get; set; } = 32;

        /// <summary> 无人租用的空池到期关闭并释放原型 </summary>
        public TimeSpan IdleLifetime { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary> 在用实例配额 (Active+Preparing+Creating)；null 表示无上限；0 非法 </summary>
        public int? MaxActivePerResource { get; set; }

        public void Validate()
        {
            if (MaxIdlePerResource < 0)
            {
                throw new ArgumentException("MaxIdlePerResource 不能为负");
            }
            if (IdleLifetime < TimeSpan.Zero)
            {
                throw new ArgumentException("IdleLifetime 不能为负");
            }
            if (MaxActivePerResource is int max && max <= 0)
            {
                throw new ArgumentException("MaxActivePerResource 必须为正或 null (0 不代表无限)");
            }
        }

        public static PoolPolicy Default { get; } = new PoolPolicy();
    }
}
