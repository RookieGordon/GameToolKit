/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 加载器契约 (P0, §3.4)。两阶段：ResolveAsync 负责定位与身份 (不交出需框架释放的对象)，
 *                LoadAsync 执行实际加载并交出 LoadedAsset 所有权。加载器不创建业务引用、不计业务引用数。
 *                成功返回 LoadedAsset 的瞬间所有权转交框架；此前异常必须自行回退本次取得的资源；
 *                成功之后即使所有调用者取消，框架也会调用 ReleaseAsync (至多一次)。
 */

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common.Resource
{
    /// <summary> 一次加载请求的不可变描述 </summary>
    public readonly struct ResourceRequest
    {
        public readonly string LoaderId;
        public readonly string Address;
        public readonly Type RequestedType;
        public readonly object? Parameters;

        public ResourceRequest(string loaderId, string address, Type requestedType, object? parameters)
        {
            LoaderId = loaderId ?? "";
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
    /// 解析结果：LocalKey 参与资源身份 (与 LoaderId 组合成 ResourceKey)；Payload 仅原加载器解释，
    /// 必须不可变且不拥有待释放资源。
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
    /// 加载器向框架转交的一次底层加载所有权。Value 为资源对象；IsAlive 纯查询 (默认 value != null，
    /// Unity 加载器使用 Unity 存活语义)；ReleaseAsync 由框架至多启动一次，完成即所有权归还。
    /// </summary>
    public sealed class LoadedAsset
    {
        public readonly object Value;
        public readonly long? EstimatedBytes;
        public readonly Func<bool> IsAlive;
        public readonly Func<Task> ReleaseAsync;

        public LoadedAsset(object value, long? estimatedBytes, Func<bool>? isAlive, Func<Task>? releaseAsync)
        {
            Value = value;
            EstimatedBytes = estimatedBytes;
            IsAlive = isAlive ?? new Func<bool>(() => value != null);
            ReleaseAsync = releaseAsync ?? new Func<Task>(() => Task.CompletedTask);
        }

        /// <summary> 便捷构造：无底层释放动作的资源 (如共享 byte[]) </summary>
        public static LoadedAsset FromUnmanaged(object value, long? estimatedBytes = null)
        {
            return new LoadedAsset(value, estimatedBytes, null, null);
        }
    }

    public interface IResourceLoader
    {
        /// <summary> 定位与身份：返回 LocalKey 与 Payload；可内部缓存清单，可异步。解析取消时清理自身临时资源 </summary>
        Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// 实际加载。operationToken 是框架独立创建的操作令牌，不是任何业务调用者的取消令牌。
        /// Unity API 操作必须通过集成层的执行上下文执行。
        /// </summary>
        Task<LoadedAsset> LoadAsync(
            ResolvedResource resource,
            IProgress<ResourceProgress> progress,
            CancellationToken operationToken);
    }
}
