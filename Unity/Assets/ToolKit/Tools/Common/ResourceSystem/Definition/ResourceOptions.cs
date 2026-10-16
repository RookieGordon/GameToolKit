/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 请求、来源加载与实例池配置。每项配置都说明作用范围：加载并发按已解析不同资源计数，
 *                时间预算按单次业务调用，池只按闲置实例计数。资源内存容量由业务显式卸载，不在此配置。
 */

using System;

namespace ToolKit.Tools.Common
{
    /// <summary> 单次请求的可选覆盖；影响结果的参数由加载器纳入身份 </summary>
    public sealed class RequestOptions
    {
        /// <summary> 本次调用的时间预算 (含解析与排队等待)；null 表示使用系统默认 </summary>
        public TimeSpan? Timeout { get; set; }

        public IProgress<ResourceProgress>? Progress { get; set; }

        /// <summary> 加载器专属参数；核心只透传，不读取其内容 </summary>
        public object? Parameters { get; set; }
    }

    /// <summary>
    /// 系统级配置；管理器装配时保存独立快照，后续修改配置对象不改变运行中的策略。
    /// </summary>
    public sealed class ResourceSystemOptions
    {
        public string DefaultFactory { get; set; } = "gameObject";

        /// <summary>
        /// 单次业务调用的默认时间预算 (含解析、等待额度、共享等待与接续、交付)；null 表示不限制。
        /// 租用接受同一配置，覆盖原型获取、实例创建等实际采用的阶段。
        /// </summary>
        public TimeSpan? RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        public PoolPolicy Pool { get; set; } = new PoolPolicy();

        public void Validate()
        {
            if (RequestTimeout is TimeSpan t && t <= TimeSpan.Zero)
            {
                throw new ArgumentException("RequestTimeout 必须为正或 null");
            }
            Pool.Validate();
        }

        /// <summary> 保存独立快照：调用方之后改写传入对象不影响运行中的策略 </summary>
        public static ResourceSystemOptions Clone(ResourceSystemOptions source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return new ResourceSystemOptions
            {
                DefaultFactory = source.DefaultFactory,
                RequestTimeout = source.RequestTimeout,
                Pool = PoolPolicy.Clone(source.Pool),
            };
        }
    }

    /// <summary>
    /// 一次共享加载尝试真实失败后，本轮等待者如何结算。
    /// <b>固定按来源决定，不是可覆盖配置</b>：AB / Resources / Local 属于同一份本地内容，
    /// 失败通常对该批一致；Remote 的失败往往只对本次尝试成立，其余请求保留接续机会。
    /// 个人取消、超时与生命周期退出不走这张表，各自按自身规则结束。
    /// </summary>
    public enum LoadFailureSpread
    {
        /// <summary> 当前共享批次中仍有效的接收者共同结束，收到同一个真实原因 </summary>
        Batch = 0,

        /// <summary> 仅本轮承担者失败；其余仍符合预算与尝试条件的请求保留接续机会 </summary>
        LeaderOnly = 1,
    }

    /// <summary> 每个资源来源的加载策略；注册时保存独立快照 </summary>
    public sealed class LoaderPolicy
    {
        /// <summary> 该来源同时执行的实际加载数；0 表示不限制，负数非法 </summary>
        public int MaxConcurrentLoads { get; set; } = 4;

        public void Validate()
        {
            if (MaxConcurrentLoads < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxConcurrentLoads));
            }
        }

        public static LoaderPolicy Clone(LoaderPolicy source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return new LoaderPolicy
            {
                MaxConcurrentLoads = source.MaxConcurrentLoads,
            };
        }
    }

    /// <summary> 实例池策略；每项作用于一个池桶 (原型身份 + 工厂身份 + 实例表示身份) </summary>
    public sealed class PoolPolicy
    {
        /// <summary> 每桶闲置实例上限；0 表示归还即销毁 </summary>
        public int MaxIdlePerResource { get; set; } = 32;

        /// <summary> 闲置实例距归还超过该时长即销毁；0 表示归还后在下一次维护时销毁 </summary>
        public TimeSpan IdleLifetime { get; set; } = TimeSpan.FromSeconds(60);

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
        }

        public static PoolPolicy Clone(PoolPolicy source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return new PoolPolicy
            {
                MaxIdlePerResource = source.MaxIdlePerResource,
                IdleLifetime = source.IdleLifetime,
            };
        }
    }
}
