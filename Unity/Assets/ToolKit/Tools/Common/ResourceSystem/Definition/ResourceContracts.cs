/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 资源身份与四份扩展契约 (需求第 1、7 条)。
 *                身份 = 来源 + 加载器返回的 LocalKey；加载器负责保证相同身份得到兼容表示。
 *                加载器两阶段：ResolveAsync 只回答"这是什么"，LoadAsync 才交出需要框架释放的所有权。
 */

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 业务已知的资源来源。装配时绑定到实际加载器；不规定目录、传输方式或加载器实现。
    /// 每次请求必须显式指定来源。同一来源可以在配置期替换加载器实现。
    /// </summary>
    public enum ResourceSource
    {
        /// <summary> Unity Resources 资源。 </summary>
        Resources = 1,

        /// <summary> AssetBundle 资源；框架只内置契约，包的管理由项目业务层维护。 </summary>
        AssetBundle = 2,

        /// <summary> 本地文件资源。 </summary>
        Local = 3,

        /// <summary> 远端资源；协议与本地复用由加载器处理。 </summary>
        Remote = 4,
    }

    /// <summary> 资源身份：来源 + 加载器解析身份。比较规则 Ordinal，不统一改小写或删查询参数。 </summary>
    public readonly struct ResourceKey : IEquatable<ResourceKey>
    {
        public readonly ResourceSource Source;
        public readonly string LocalKey;

        public ResourceKey(ResourceSource source, string localKey)
        {
            Source = source;
            LocalKey = localKey ?? "";
        }

        public bool Equals(ResourceKey other)
        {
            return Source == other.Source
                   && string.Equals(LocalKey, other.LocalKey, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is ResourceKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Source * 397) ^ LocalKey.GetHashCode();
            }
        }

        public override string ToString()
        {
            return Source + ":" + LocalKey;
        }
    }

    /// <summary> 池身份：原型 ResourceKey + 工厂标识 + 实例表示标识 (工厂 GetInstanceKey 提供) </summary>
    public readonly struct PoolKey : IEquatable<PoolKey>
    {
        public readonly ResourceKey Prototype;
        public readonly string FactoryId;
        public readonly string InstanceKey;

        public PoolKey(ResourceKey prototype, string factoryId, string instanceKey)
        {
            Prototype = prototype;
            FactoryId = factoryId ?? "";
            InstanceKey = instanceKey ?? "";
        }

        public bool Equals(PoolKey other)
        {
            return Prototype.Equals(other.Prototype)
                   && string.Equals(FactoryId, other.FactoryId, StringComparison.Ordinal)
                   && string.Equals(InstanceKey, other.InstanceKey, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is PoolKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Prototype.GetHashCode();
                hash = (hash * 397) ^ FactoryId.GetHashCode();
                hash = (hash * 397) ^ InstanceKey.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
        {
            return Prototype + "|" + FactoryId + "|" + InstanceKey;
        }
    }

    /// <summary> 一次加载请求的不可变描述；由 来源/地址/目标类型/额外参数 唯一确定 </summary>
    public readonly struct ResourceRequest
    {
        public readonly ResourceSource Source;
        public readonly string Address;
        public readonly Type RequestedType;

        /// <summary> 加载器专属、调用期间不可变的参数；核心不读取其内容 </summary>
        public readonly object? Parameters;

        public ResourceRequest(ResourceSource source, string address, Type requestedType, object? parameters)
        {
            Source = source;
            Address = address ?? "";
            RequestedType = requestedType;
            Parameters = parameters;
        }
    }

    /// <summary> 加载进度；总量未知时 TotalBytes 为 null，不得伪造百分比 </summary>
    public readonly struct ResourceProgress
    {
        public readonly LoadStage Stage;
        public readonly long CompletedBytes;
        public readonly long? TotalBytes;

        public ResourceProgress(LoadStage stage, long completedBytes, long? totalBytes)
        {
            Stage = stage;
            CompletedBytes = completedBytes;
            TotalBytes = totalBytes;
        }
    }

    /// <summary>
    /// 解析结果：LocalKey 参与资源身份；Payload 仅原加载器解释，必须不可变且不拥有待释放资源。
    /// </summary>
    public sealed class ResolvedResource
    {
        public readonly string LocalKey;
        public readonly Type RepresentationType;
        public readonly object? Payload;

        public ResolvedResource(string localKey, Type representationType, object? payload = null)
        {
            LocalKey = localKey;
            RepresentationType = representationType;
            Payload = payload;
        }
    }

    /// <summary>
    /// 一次**加载**的内容及其约定的释放责任。<b>它不是最终资产</b>——需要转换时由文件转换器把它变成业务对象。
    /// <para>
    /// 形态由来源决定，转换器只认它能处理的那一种：字节数组 / 已解码文本 / 文件流 /
    /// <c>UnityEngine.Object</c> / AssetBundle 取出的对象 / 引擎共享资产。
    /// 因此转换器不必只接受路径——取内容归加载器，转换器按形态处理。
    /// </para>
    /// </summary>
    public class LoadedContent
    {
        public readonly object Value;
        public readonly Func<Task> ReleaseAsync;

        public LoadedContent(object value, Func<Task>? releaseAsync = null)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            ReleaseAsync = releaseAsync ?? new Func<Task>(() => Task.CompletedTask);
        }
    }

    /// <summary>
    /// 带内容形态的加载内容：加载器直接写出"我交的是字节数组 / 文本 / 文件流 / 引擎对象"。
    /// 释放责任仍由本对象承担；转换器取 <see cref="LoadedContent.Value"/> 或按已知形态直接使用。
    /// </summary>
    public sealed class LoadedContent<T> : LoadedContent where T : class
    {
        public LoadedContent(T value, Func<Task>? releaseAsync = null) : base(value, releaseAsync)
        {
        }

        /// <summary> 按已知形态取内容，免去转换器里的类型转换 </summary>
        public new T Value => (T)base.Value;
    }

    /// <summary>
    /// 一次**转换**的产物 (最终交给业务的资产) 及其约定的释放责任。
    /// IsAlive 为纯查询 (默认 value != null，Unity 转换器用引擎存活语义)；
    /// ReleaseAsync 由框架至多启动一次。外部持有的资产不提供释放回调时，框架不推断自己拥有其底层对象。
    /// </summary>
    public sealed class LoadedAsset
    {
        public readonly object Value;
        public readonly Func<bool> IsAlive;
        public readonly Func<Task> ReleaseAsync;

        public LoadedAsset(object value, Func<bool>? isAlive = null, Func<Task>? releaseAsync = null)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            IsAlive = isAlive ?? new Func<bool>(() => value != null);
            ReleaseAsync = releaseAsync ?? new Func<Task>(() => Task.CompletedTask);
        }

        /// <summary> 便捷构造：无底层释放动作的资源 (如共享 byte[]) </summary>
        public static LoadedAsset FromUnmanaged(object value)
        {
            return new LoadedAsset(value);
        }

        /// <summary> 由加载内容直接作为最终资产：无转换步骤的来源 (如引擎共享对象) 使用 </summary>
        public static LoadedAsset FromContent(LoadedContent content, Func<bool>? isAlive = null)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            return new LoadedAsset(content.Value, isAlive, content.ReleaseAsync);
        }
    }

    /// <summary>
    /// 加载器契约：一种来源一份实现。<b>只负责把内容取到本地或内存</b>，交出内容与释放责任；
    /// 不创建业务引用、不计业务引用数、不做类型转换。
    /// </summary>
    public interface IResourceLoader
    {
        /// <summary> 定位与身份：返回 LocalKey 与 Payload；可异步。解析取消时自行清理临时资源。 </summary>
        Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// 实际加载：交出取到的内容。operationToken 是框架独立创建的操作令牌，
        /// 不是任何业务调用者的取消令牌。Unity API 操作必须通过接入层的执行上下文执行。
        /// </summary>
        Task<LoadedContent> LoadAsync(
            ResolvedResource resource,
            IProgress<ResourceProgress> progress,
            CancellationToken operationToken);
    }

    /// <summary> 实例工厂契约：把租用请求变成原型请求，再从原型创建与回收实例。 </summary>
    public interface IInstanceFactory
    {
        /// <summary> 将租用请求转为原型请求；必须明确返回 Source。 </summary>
        ResourceRequest GetPrototypeRequest(ResourceRequest instanceRequest);

        /// <summary> 实例表示标识：按结果类型和影响实例内容的参数提供稳定值；普通工厂用固定值。 </summary>
        string GetInstanceKey(ResourceRequest instanceRequest);

        /// <summary> 该原型能否实例化出目标类型的实例。必须无副作用且不阻塞。 </summary>
        bool CanCreate(object prototype, Type instanceType);

        /// <summary> 创建实例；失败自行回退，回退失败通过 ResourceLoadException.CleanupRemainders 交出残留。 </summary>
        Task<object> CreateAsync(object prototype, ResourceRequest creationRequest, CancellationToken operationToken);

        /// <summary> 实例是否仍存活 (未被引擎销毁)。必须无副作用且不阻塞。 </summary>
        bool IsAlive(object instance);

        /// <summary> 租用重置，在状态串行域内同步完成。 </summary>
        void OnRent(object instance);

        /// <summary> 归还重置，在状态串行域内同步完成。 </summary>
        void OnReturn(object instance);

        /// <summary> 销毁并确认结束；Unity Destroy 延后完成时必须等到真实结束。 </summary>
        Task DestroyAsync(object instance);
    }

    /// <summary>
    /// 需要"内容 → 业务对象"这一步的加载器实现它。只有文件类来源需要：
    /// 引擎来源（Resources、AssetBundle 取出的对象）本身就是业务要的东西，不必转换。
    /// 由实现决定用哪个转换器，编排层不解释内容。
    /// </summary>
    internal interface IConvertingLoader
    {
        /// <summary> 把加载得到的内容转换成最终资产；转换差异标识已随解析结果带回 </summary>
        Task<LoadedAsset> ConvertAsync(
            LoadedContent content, ResolvedResource resolved, CancellationToken operationToken);
    }

    /// <summary>
    /// 文件转换契约：把**已取到的内容**变成业务对象 (需求第 7 条)。本地与远端文件加载器共用，
    /// 按目标类型选择实现。<b>转换器不读文件、不接触文件路径</b>——取内容归加载器。
    /// </summary>
    public interface IFileResourceConverter
    {
        /// <summary>
        /// 输入是**已取到的内容** (加载器交出的 LoadedContent)，输出是业务资产。
        /// 转换器不读文件、不接触文件路径：取内容归加载器，造对象归这里。
        /// conversionKey 是请求提供的转换差异标识，由具体实现解释，并参与资源缓存身份。
        /// 失败时回收中间对象；释放失败时通过 ResourceLoadException.CleanupRemainders 交出实际残留。
        /// </summary>
        Task<LoadedAsset> ConvertAsync(
            LoadedContent content, Type resultType, string? conversionKey, CancellationToken cancellationToken);
    }
}
