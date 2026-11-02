/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 装配配置：执行上下文、各来源加载器及其策略、转换器、实例工厂与池策略。
 *                ResourceSystem 在构造时取走独立快照，之后修改本对象不影响运行中的系统。
 *                资源缓存由业务显式卸载，不配置内存容量。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 每个资源来源的加载策略 </summary>
    public readonly struct LoaderPolicy
    {
        /// <summary> 同时进行的实际加载数；同键等待者合并为一次，不重复占额。必须大于 0 </summary>
        public readonly int MaxConcurrentLoads;

        /// <summary> 单个请求从入口到交付的总超时，覆盖排队、共享等待与接续；null 表示不限 </summary>
        public readonly TimeSpan? LoadTimeout;

        public LoaderPolicy(int maxConcurrentLoads, TimeSpan? loadTimeout)
        {
            if (maxConcurrentLoads <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrentLoads));
            if (loadTimeout.HasValue && loadTimeout.Value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(loadTimeout));
            MaxConcurrentLoads = maxConcurrentLoads;
            LoadTimeout = loadTimeout;
        }

        public static LoaderPolicy Default => new LoaderPolicy(4, TimeSpan.FromSeconds(30));
    }

    /// <summary> 实例池策略 </summary>
    public readonly struct PoolPolicy
    {
        /// <summary> 每个原型保留的闲置实例上限；0 表示归还即销毁 </summary>
        public readonly int MaxIdlePerPrototype;

        public PoolPolicy(int maxIdlePerPrototype)
        {
            if (maxIdlePerPrototype < 0) throw new ArgumentOutOfRangeException(nameof(maxIdlePerPrototype));
            MaxIdlePerPrototype = maxIdlePerPrototype;
        }

        public static PoolPolicy Default => new PoolPolicy(32);
    }

    /// <summary> 一个来源的装配项 </summary>
    public readonly struct LoaderSetup
    {
        public readonly ResourceSource Source;
        public readonly ILoader Loader;
        public readonly LoaderPolicy Policy;

        public LoaderSetup(ResourceSource source, ILoader loader, LoaderPolicy policy)
        {
            Source = source;
            Loader = loader ?? throw new ArgumentNullException(nameof(loader));
            Policy = policy;
        }
    }

    /// <summary> 系统级装配配置 </summary>
    public sealed class ResourceSystemOptions
    {
        /// <summary> 引擎调用所在的执行上下文 (转换、实例化、销毁)；默认内联执行 </summary>
        public IExecutionContext ExecutionContext { get; set; } = ImmediateExecutionContext.Instance;

        /// <summary> 各来源的加载器；同一来源后注册的覆盖先注册的 </summary>
        public Dictionary<ResourceSource, LoaderSetup> Loaders { get; } = new Dictionary<ResourceSource, LoaderSetup>();

        /// <summary> 按目标类型注册的转换器 </summary>
        public Dictionary<Type, IProductConverter> Converters { get; } = new Dictionary<Type, IProductConverter>();

        /// <summary> 实例工厂；为 null 时不提供租用 </summary>
        public IInstanceFactory InstanceFactory { get; set; }

        public PoolPolicy Pool { get; set; } = PoolPolicy.Default;

        /// <summary> 初始化时需要一起完成的准备工作 (如文件缓存的启动清理)；失败则整体初始化失败，可重试 </summary>
        public List<Func<CancellationToken, Task>> Initializers { get; } = new List<Func<CancellationToken, Task>>();

        public ResourceSystemOptions AddLoader(ResourceSource source, ILoader loader, LoaderPolicy? policy = null)
        {
            Loaders[source] = new LoaderSetup(source, loader, policy ?? LoaderPolicy.Default);
            return this;
        }

        public ResourceSystemOptions AddConverter<T>(IProductConverter converter)
        {
            Converters[typeof(T)] = converter ?? throw new ArgumentNullException(nameof(converter));
            return this;
        }
    }
}
