using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;

namespace UnityToolKit.Runtime.ResourceSystem
{
    /// <summary>
    /// 保存装配参数，在 Unity 主线程调用 ConvertToOptions 创建运行时对象。
    /// 自定义配置派生下方对应类型并标记 [Serializable]，即可在 Inspector 中选择。
    /// 工厂应返回独立实例或明确借用的外部服务；外部服务的关闭责任仍属于其创建者。
    /// </summary>
    [CreateAssetMenu(fileName = "ResourceSystemConfig", menuName = "UnityToolKit/Resource System Config")]
    public class ResourceSystemConfig : ScriptableObject
    {
        [SerializeReference] public ExecutionContextConfig ExecutionContext = new UnityExecutionContextConfig();
        /// <summary> 各来源的配置；InitializeOnStartup 决定是否在初始化时创建实例，策略始终保留。 </summary>
        [SerializeReference] public List<LoaderConfig> Loaders = new List<LoaderConfig>();
        [SerializeReference] public List<ConverterConfig> Converters = new List<ConverterConfig>();
        [SerializeReference] public InstanceFactoryConfig InstanceFactory;

        [Min(0), Tooltip("每个原型最多保留的闲置实例数；0 表示归还即销毁。")]
        public int MaxIdlePerPrototype = PoolPolicy.Default.MaxIdlePerPrototype;

        [SerializeReference] public List<InitializerConfig> Initializers = new List<InitializerConfig>();

        /// <summary>
        /// 每次返回独立 Options；这里只装配，不执行初始化回调。
        /// 同来源加载器、同目标类型转换器按列表顺序注册，后项覆盖前项，与 Options 一致。
        /// </summary>
        public ResourceSystemOptions ConvertToOptions()
        {
            Validate();
            var options = new ResourceSystemOptions
            {
                ExecutionContext = Create(ExecutionContext.CreateExecutionContext, nameof(ExecutionContext)),
                Pool = new PoolPolicy(MaxIdlePerPrototype),
                InstanceFactory = InstanceFactory == null ? null : Create(InstanceFactory.CreateFactory, nameof(InstanceFactory))
            };
            for (var i = 0; i < Loaders.Count; i++)
            {
                var config = Loaders[i];
                // 先确定最终来源条目；被覆盖的配置不能参与依赖创建、缓存清理或取得服务所有权。
                var replaced = false;
                for (var next = i + 1; next < Loaders.Count; next++)
                    if (Loaders[next].Source == config.Source) { replaced = true; break; }
                if (replaced) continue;

                if (config.InitializeOnStartup)
                    InstallLoader(config, options, $"Loaders[{i}]");
                else
                    options.AddExternalLoader(config.Source, config.CreatePolicy());
            }
            for (var i = 0; i < Converters.Count; i++)
            {
                var config = Converters[i];
                options.Converters[config.TargetType] = Create(config.CreateConverter, $"Converters[{i}]");
            }
            for (var i = 0; i < Initializers.Count; i++)
                options.Initializers.Add(Create(Initializers[i].CreateInitializer, $"Initializers[{i}]"));
            return options;
        }

        /// <summary> 仅验证配置数据，供 Inspector 与转换入口共用；不创建运行时对象或执行回调。 </summary>
        public void Validate()
        {
            if (MaxIdlePerPrototype < 0)
                throw new InvalidOperationException("MaxIdlePerPrototype 必须大于或等于 0。");
            ValidateEntry(ExecutionContext, nameof(ExecutionContext));
            ValidateEntries(Loaders, nameof(Loaders));
            ValidateEntries(Converters, nameof(Converters));
            if (InstanceFactory != null) ValidateEntry(InstanceFactory, nameof(InstanceFactory));
            ValidateEntries(Initializers, nameof(Initializers));
        }

        private static void ValidateEntries<T>(List<T> entries, string field) where T : ResourceSystemOptionConfig
        {
            if (entries == null) throw new InvalidOperationException(field + " 列表不能为空；不需要时请使用空列表。");
            for (var i = 0; i < entries.Count; i++) ValidateEntry(entries[i], $"{field}[{i}]");
        }

        private static void ValidateEntry(ResourceSystemOptionConfig entry, string field)
        {
            if (entry == null) throw new InvalidOperationException(field + " 未选择配置类型，或原类型已丢失。");
            try
            {
                // 公共契约在入口检查，不依赖自定义 Validate 是否调用 base。
                if (entry is LoaderConfig loader)
                {
                    loader.CreatePolicy();
                    if (!loader.InitializeOnStartup) return;
                }
                if (entry is ConverterConfig converter && converter.TargetType == null)
                    throw new InvalidOperationException("目标类型不能为空。");
                entry.Validate();
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(field + ": " + error.Message, error);
            }
        }

        private static void InstallLoader(LoaderConfig config, ResourceSystemOptions options, string field)
        {
            try
            {
                config.Install(options);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(field + " 创建失败：" + error.Message, error);
            }
        }

        private static T Create<T>(Func<T> factory, string field) where T : class
        {
            try
            {
                return factory() ?? throw new InvalidOperationException("工厂返回了 null。");
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(field + " 创建失败：" + error.Message, error);
            }
        }
    }

    /// <summary>
    /// 只保存 Unity 可序列化数据；Validate 不得创建运行时对象或产生副作用。
    /// 自定义具体类须位于运行时程序集，标记 [Serializable] 并提供公开无参构造函数。
    /// </summary>
    [Serializable]
    public abstract class ResourceSystemOptionConfig
    {
        public virtual void Validate() { }
    }

    [Serializable]
    public abstract class ExecutionContextConfig : ResourceSystemOptionConfig
    {
        public abstract IExecutionContext CreateExecutionContext();
    }

    [Serializable]
    public sealed class UnityExecutionContextConfig : ExecutionContextConfig
    {
        public override IExecutionContext CreateExecutionContext() => new UnityExecutionContext();
    }

    /// <summary> 仅用于不调用 Unity API 的装配；不会把调用切换到 Unity 主线程。 </summary>
    [Serializable]
    public sealed class ImmediateExecutionContextConfig : ExecutionContextConfig
    {
        public override IExecutionContext CreateExecutionContext() => ImmediateExecutionContext.Instance;
    }

    [Serializable]
    public class LoaderConfig : ResourceSystemOptionConfig
    {
        public ResourceSource Source = ResourceSource.Resources;

        [Tooltip("勾选时在初始化阶段创建加载器；取消时仅保存来源策略，稍后由业务注册实例。")]
        public bool InitializeOnStartup = true;

        [Min(1), Tooltip("这个来源同时进行的实际加载数；同键等待者合并后只占一个名额。")]
        public int MaxConcurrentLoads = LoaderPolicy.Default.MaxConcurrentLoads;

        [Min(0), Tooltip("单次请求的总超时（秒），包括排队；0 表示不限时。")]
        public double TimeoutSeconds = LoaderPolicy.Default.LoadTimeout?.TotalSeconds ?? 0;

        public LoaderPolicy CreatePolicy()
        {
            if (double.IsNaN(TimeoutSeconds) || double.IsInfinity(TimeoutSeconds)
                || TimeoutSeconds < 0 || TimeoutSeconds > int.MaxValue / 1000d)
                throw new InvalidOperationException("TimeoutSeconds 必须为 0（不限时），或不超过 2147483.647 的正数。");
            return new LoaderPolicy(MaxConcurrentLoads, TimeoutSeconds == 0 ? (TimeSpan?)null : TimeSpan.FromSeconds(TimeoutSeconds));
        }

        public override void Validate()
        {
            if (InitializeOnStartup && GetType() == typeof(LoaderConfig))
                throw new InvalidOperationException("初始化时创建实例需要选择具体加载器类型；仅保存策略时请取消该开关。");
        }

        /// <summary> 模板方法：将本来源实例与关联准备/关闭动作装入 Options；简单加载器只需重写 CreateLoader。 </summary>
        public virtual void Install(ResourceSystemOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var loader = CreateLoader() ?? throw new InvalidOperationException("工厂返回了 null。");
            options.AddLoader(Source, loader, CreatePolicy());
        }

        /// <summary> 具体配置重写此方法；关闭 InitializeOnStartup 时不会调用。 </summary>
        public virtual ILoader CreateLoader()
            => throw new InvalidOperationException("请选择具体加载器类型，或取消“初始化时创建实例”。");
    }

    [Serializable]
    public sealed class ResourcesLoaderConfig : LoaderConfig
    {
        [Tooltip("释放产物时对非 GameObject/Component 资产调用 Resources.UnloadAsset；默认由 Unity 管理共享资产。")]
        public bool UnloadAssetOnRelease;

        public override ILoader CreateLoader() => new ResourcesLoader(UnloadAssetOnRelease);
    }

    public enum LocalFileDirectory
    {
        WorkingDirectory,
        StreamingAssets,
        PersistentData,
        Custom
    }

    [Serializable]
    public sealed class LocalFileLoaderConfig : LoaderConfig
    {
        public LocalFileDirectory Directory = LocalFileDirectory.WorkingDirectory;

        [Tooltip("Directory 为 Custom 时使用的本地目录。StreamingAssets 仅适用于可通过文件 API 直接读取的平台。")]
        public string CustomDirectory;

        public LocalFileLoaderConfig() { Source = ResourceSource.Local; }

        public override void Validate()
        {
            if (!Enum.IsDefined(typeof(LocalFileDirectory), Directory))
                throw new InvalidOperationException("请选择有效的本地文件目录。");
            if (Directory == LocalFileDirectory.Custom && string.IsNullOrWhiteSpace(CustomDirectory))
                throw new InvalidOperationException("CustomDirectory 不能为空。");
        }

        public override ILoader CreateLoader()
        {
            switch (Directory)
            {
                case LocalFileDirectory.WorkingDirectory: return new LocalFileLoader();
                case LocalFileDirectory.StreamingAssets: return new LocalFileLoader(Application.streamingAssetsPath);
                case LocalFileDirectory.PersistentData: return new LocalFileLoader(Application.persistentDataPath);
                case LocalFileDirectory.Custom: return new LocalFileLoader(CustomDirectory);
                default: throw new InvalidOperationException("请选择有效的本地文件目录。");
            }
        }
    }

    [Serializable]
    public abstract class ConverterConfig : ResourceSystemOptionConfig
    {
        /// <summary> 实际注册的目标类型，由配置类声明，不保存程序集名字符串。 </summary>
        public abstract Type TargetType { get; }
        public abstract IProductConverter CreateConverter();
    }

    [Serializable]
    public sealed class TextConverterConfig : ConverterConfig
    {
        [Tooltip("文本编码名，例如 utf-8、utf-16；无法解码的字节会报错。")]
        public string EncodingName = "utf-8";

        public override Type TargetType => typeof(string);
        public override void Validate() => CreateEncoding();
        public override IProductConverter CreateConverter() => new TextConverter(CreateEncoding());

        private Encoding CreateEncoding() => Encoding.GetEncoding(EncodingName,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    [Serializable]
    public sealed class TextureConverterConfig : ConverterConfig
    {
        [Tooltip("保留纹理的 CPU 可读副本；关闭可节省内存。")]
        public bool Readable;

        public override Type TargetType => typeof(Texture2D);
        public override IProductConverter CreateConverter() => new TextureConverter(Readable);
    }

    [Serializable]
    public abstract class InstanceFactoryConfig : ResourceSystemOptionConfig
    {
        public abstract IInstanceFactory CreateFactory();
    }

    [Serializable]
    public sealed class GameObjectFactoryConfig : InstanceFactoryConfig
    {
        public override IInstanceFactory CreateFactory() => new GameObjectFactory();
    }

    [Serializable]
    public abstract class InitializerConfig : ResourceSystemOptionConfig
    {
        /// <summary>
        /// 创建准备工作委托，交给 ResourceSystem.InitializeAsync 执行，不在此处启动工作。
        /// 委托应捕获本次配置快照；有生命周期的服务由业务持有并负责关闭。
        /// </summary>
        public abstract Func<CancellationToken, Task> CreateInitializer();
    }
}
