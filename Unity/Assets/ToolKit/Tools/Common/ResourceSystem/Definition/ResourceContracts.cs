#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 业务已知的资源来源。装配时绑定到实际加载器；不规定目录、传输方式或加载器实现。
    /// 每次请求必须显式指定来源。同一来源可以在配置期或运行期注册、替换加载器实现。
    /// </summary>
    public enum ResourceSource
    {
        /// <summary> Unity Resources 资源。 </summary>
        Resources = 1,

        /// <summary> AssetBundle 资源；框架只内置契约，包的管理由项目业务层维护。 </summary>
        AssetBundle = 2,

        /// <summary> 本地文件资源。 </summary>
        Local = 3,

        /// <summary> 远端资源；落盘与本地复用由远程加载器处理。 </summary>
        Remote = 4,
    }

    /// <summary>
    /// 资源身份 = 来源 + 地址 + 目标类型 + 加载参数。身份相同的请求共享一次实际加载、共享一份产物。
    /// Parameters 以 Equals 参与身份：影响结果的参数请放进它并正确实现 Equals/GetHashCode；
    /// 不实现时按引用比较，结果仍正确，只是失去共享。
    /// </summary>
    public readonly struct ResourceKey : IEquatable<ResourceKey>
    {
        public readonly ResourceSource Source;
        public readonly string Address;
        /// <summary> 业务最终要拿到的类型；加载器产物不是该类型时经转换器转换 </summary>
        public readonly Type RequestedType;
        /// <summary> 加载器专属参数；核心只拿它参与身份比较，不读取其内容 </summary>
        public readonly object? Parameters;

        public ResourceKey(ResourceSource source, string address, Type requestedType, object? parameters = null)
        {
            if (string.IsNullOrEmpty(address)) throw new ArgumentException("地址不能为空", nameof(address));
            Source = source;
            Address = address;
            RequestedType = requestedType ?? throw new ArgumentNullException(nameof(requestedType));
            Parameters = parameters;
        }

        public bool Equals(ResourceKey other)
        {
            return Source == other.Source
                   && string.Equals(Address, other.Address, StringComparison.Ordinal)
                   && RequestedType == other.RequestedType
                   && Equals(Parameters, other.Parameters);
        }

        public override bool Equals(object? obj) => obj is ResourceKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)Source;
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(Address);
                hash = hash * 31 + RequestedType.GetHashCode();
                hash = hash * 31 + (Parameters != null ? Parameters.GetHashCode() : 0);
                return hash;
            }
        }

        public override string ToString() => $"{Source}:{Address}<{RequestedType.Name}>";
    }

    /// <summary> 一次进度快照。Fraction 为 null 表示总量未知，只有阶段文字。 </summary>
    public sealed class OperationProgress
    {
        public OperationProgress(double? fraction, string? message = null)
        {
            if (fraction.HasValue && (double.IsNaN(fraction.Value) || double.IsInfinity(fraction.Value)
                || fraction.Value < 0 || fraction.Value > 1))
            {
                throw new ArgumentOutOfRangeException(nameof(fraction), "进度必须为 0 到 1 之间的有限数值，未知总量时使用 null。");
            }

            Fraction = fraction;
            Message = message;
        }

        public double? Fraction { get; }
        public string? Message { get; }
    }

    /// <summary>
    /// 一份具体产物：对象本身加上它唯一的一份释放责任。
    /// 加载器、转换器把产物交给核心后，释放只由核心在业务卸载/退出/迟到清理时执行。
    /// 后端不交出释放责任时 (如默认 Resources 资产)，Release 为空操作，核心不推断自己拥有底层对象。
    /// </summary>
    public interface IResource
    {
        object Value { get; }

        /// <summary> 释放底层对象；失败抛出异常，由核心记录残留以便后续显式卸载重试。 </summary>
        void Release();
    }

    /// <summary> IResource 的通用实现：对象 + 可选的释放动作 </summary>
    public sealed class Product : IResource
    {
        private readonly Action? _release;

        public Product(object value, Action? release = null)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            _release = release;
        }

        public object Value { get; }

        public void Release() => _release?.Invoke();
    }

    /// <summary>
    /// 一种来源的定位与生产。只负责按身份产出一份产物，不管共享、计数与等待者。
    /// 在系统的执行上下文内启动（如 Unity 主线程），可直接使用引擎异步 API，不得同步阻塞线程；文件/网络 IO 应使用异步操作。
    /// 收到的 cancellation 是"这一轮实际尝试"的取消（最后一个等待者退出或系统关闭时触发），实现应观察它。
    /// </summary>
    public interface ILoader
    {
        Task<IResource> LoadAsync(ResourceKey key, IProgress<OperationProgress>? progress, CancellationToken cancellation);
    }

    /// <summary>
    /// 产物转换器：把加载器产物 (如 byte[]) 转成业务目标类型 (如 Texture2D)。按目标类型注册，本地与远程共用。
    /// 在执行上下文内调用，可以直接使用引擎 API。输入产物由核心在转换后释放，转换器只负责输出产物及其释放。
    /// </summary>
    public interface IProductConverter
    {
        Task<IResource> ConvertAsync(IResource source, ResourceKey key, CancellationToken cancellation);
    }

    /// <summary>
    /// 实例工厂：按原型创建实例并负责实例的借出准备、归还重置与销毁。方法均在执行上下文内调用。
    /// </summary>
    public interface IInstanceFactory
    {
        /// <summary> 原型的资源类型 (如 GameObject)；租用时按该类型加载原型 </summary>
        Type PrototypeType { get; }

        object Create(object prototype);

        /// <summary> 实例交付给业务前的准备 (如 SetActive(true))；新建与复用的实例都会经过 </summary>
        void OnRent(object instance);

        /// <summary> 实例归还进池前的重置 (如 SetActive(false))；抛出异常则该实例直接销毁 </summary>
        void OnReturn(object instance);

        /// <summary> 销毁实例；失败抛出异常，由池记录残留 </summary>
        void Destroy(object instance);
    }

    /// <summary> 远程地址到本地缓存路径的映射 </summary>
    public interface IPathConverter
    {
        /// <summary> 本地缓存根目录；框架独占写入，启动清理只在该目录内进行 </summary>
        string RootDirectory { get; }

        /// <summary> 远程资源在本地的最终存储路径，必须位于 RootDirectory 内 </summary>
        string RemoteUrlToLocalPath(string url);
    }
}
