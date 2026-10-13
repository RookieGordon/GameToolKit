/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 请求、来源加载并发和实例池配置。资源缓存由业务显式卸载，不配置内存容量。
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

    /// <summary> 系统级配置；管理器保存独立快照，后续修改配置对象不改变运行中的策略 </summary>
    public sealed class ResourceSystemOptions
    {
        public string DefaultFactory { get; set; } = "gameObject";
        /// <summary> 实例池闲置维护的间隔；不触发资源缓存淘汰 </summary>
        public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan? RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
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
            Pool.Validate();
        }
    }

    /// <summary> 一次加载失败后，如何处理同资源已在等待的请求；主动取消不属于加载失败 </summary>
    public enum LoadFailurePolicy
    {
        /// <summary> 本轮等待请求一起失败；适合本地资源。后续新请求仍可重新尝试 </summary>
        FailWaitingRequests,

        /// <summary> 仅当前请求失败，其余请求继续取得执行机会；适合远端资源 </summary>
        ContinueWaitingRequests,
    }

    /// <summary> 每个资源来源的加载策略；注册时保存独立快照 </summary>
    public sealed class LoaderPolicy
    {
        /// <summary> 按已解析不同资源的实际加载计数；0 表示不限制，负数非法 </summary>
        public int MaxConcurrentLoads { get; set; } = 4;

        /// <summary> 框架按显式策略处理失败，不推断地址或传输协议 </summary>
        public LoadFailurePolicy FailurePolicy { get; set; } = LoadFailurePolicy.FailWaitingRequests;

        public void Validate()
        {
            if (MaxConcurrentLoads < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxConcurrentLoads));
            }
            if (FailurePolicy != LoadFailurePolicy.FailWaitingRequests &&
                FailurePolicy != LoadFailurePolicy.ContinueWaitingRequests)
            {
                throw new ArgumentOutOfRangeException(nameof(FailurePolicy));
            }
        }

        public static LoaderPolicy Clone(LoaderPolicy source)
        {
            return new LoaderPolicy
            {
                MaxConcurrentLoads = source.MaxConcurrentLoads,
                FailurePolicy = source.FailurePolicy,
            };
        }
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

        public static PoolPolicy Clone(PoolPolicy source)
        {
            return new PoolPolicy
            {
                MaxIdlePerResource = source.MaxIdlePerResource,
                IdleLifetime = source.IdleLifetime,
                MaxActivePerResource = source.MaxActivePerResource,
            };
        }
    }
}
