/*
 * author       : Gordon
 * datetime     : 2026-10-15
 * description  : 接入层装配。核心只接收依赖使用权：这里创建缓存、加载器、工厂与默认错误映射，
 *                并明确所有权——本类创建的共享依赖由本类关闭，注入的依赖不关闭。
 *                不含资源编排：编排在 ResourcePipeline，池在 InstancePool。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Network;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 便利装配：一次创建可用的默认环境并交出所有权。核心不创建缓存，因此只用 Unity Resources
    /// 的项目不会因为磁盘缓存初始化失败而无法加载。
    /// </summary>
    public sealed class ResourceSystemSetup : IDisposable
    {
        private readonly List<object> _owned = new List<object>();
        private int _disposed;

        /// <summary> 本装配为共享依赖提供的文件缓存；未请求缓存时为 null </summary>
        public FileCache? FileCache { get; private set; }

        /// <summary> 取得或创建文件缓存；同一装配只创建一个 </summary>
        /// <param name="options">缓存配置；<b>必须提供有效的启动清理目标</b>，否则初始化时拒绝</param>
        public FileCache GetOrCreateFileCache(FileCacheOptions options)
        {
            if (_disposed != 0) throw new ObjectDisposedException(nameof(ResourceSystemSetup));
            if (FileCache != null) return FileCache;
            FileCache = new FileCache(options);
            _owned.Add(FileCache);
            return FileCache;
        }

        /// <summary> 取得或创建文件缓存，根目录与目标容量由装配给出 </summary>
        public FileCache GetOrCreateFileCache(string directory, long startupTargetBytes)
        {
            return GetOrCreateFileCache(new FileCacheOptions
            {
                Directory = directory,
                StartupTargetBytes = startupTargetBytes,
            });
        }

        /// <summary> 关闭本装配创建的共享依赖；注入的依赖不在此关闭 </summary>
        public async Task DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var owned in _owned)
            {
                if (owned is FileCache cache) await cache.DisposeAsync().ConfigureAwait(false);
            }
            _owned.Clear();
            FileCache = null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var owned in _owned)
            {
                if (owned is FileCache cache) cache.Dispose();
            }
            _owned.Clear();
            FileCache = null;
        }
    }

    /// <summary> 用户码映射：业务可替换为本地化决策或统一收敛 </summary>
    public interface IErrorMapper
    {
        UserErrorCode Map(string diagnosticCode, LoadStage stage, IReadOnlyDictionary<string, object> context);
    }

    /// <summary> 默认映射表；未知码归为 Unknown 并原样保留诊断 </summary>
    public sealed class DefaultErrorMapper : IErrorMapper
    {
        public static readonly DefaultErrorMapper Instance = new DefaultErrorMapper();

        private static readonly Dictionary<string, UserErrorCode> Table = new Dictionary<string, UserErrorCode>
        {
            { DiagnosticCodes.LoaderNotRegistered, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.LoaderResolveFailed, UserErrorCode.ResourceUnavailable },
            { DiagnosticCodes.LoaderInvalidResult, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.AssetNotFound, UserErrorCode.ResourceUnavailable },
            { DiagnosticCodes.AssetTypeMismatch, UserErrorCode.ResourceUnavailable },
            { DiagnosticCodes.AssetInvalidated, UserErrorCode.ResourceDamaged },
            { DiagnosticCodes.AssetLoadFailed, UserErrorCode.ResourceUnavailable },
            { DiagnosticCodes.AssetConversionFailed, UserErrorCode.ResourceDamaged },
            { DiagnosticCodes.AssetUnsupportedRepresentation, UserErrorCode.ResourceUnavailable },
            { DiagnosticCodes.RequestTimeout, UserErrorCode.NetworkProblem },
            { DiagnosticCodes.InstanceUnsupported, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.InstancePoolClosed, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.InstanceCreateFailed, UserErrorCode.ResourceUnavailable },
            { DiagnosticCodes.InstanceResetFailed, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.InstanceDestroyFailed, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.LifecycleManagerDisposed, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.LifecycleReleaseFailed, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.LifecycleSystemDisposed, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.ObserverCallbackFailed, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.InternalUnexpected, UserErrorCode.Unknown },
            { DiagnosticCodes.InternalConsistency, UserErrorCode.Unknown },
            { DiagnosticCodes.NetworkInvalidUri, UserErrorCode.NetworkProblem },
            { DiagnosticCodes.NetworkDnsFailed, UserErrorCode.NetworkProblem },
            { DiagnosticCodes.NetworkConnectFailed, UserErrorCode.NetworkProblem },
            { DiagnosticCodes.NetworkTlsFailed, UserErrorCode.NetworkProblem },
            { DiagnosticCodes.NetworkTimeout, UserErrorCode.NetworkProblem },
            { DiagnosticCodes.NetworkInterrupted, UserErrorCode.NetworkProblem },
            { DiagnosticCodes.NetworkHttpNotFound, UserErrorCode.ResourceUnavailable },
            { DiagnosticCodes.NetworkHttpDenied, UserErrorCode.ResourceUnavailable },
            { DiagnosticCodes.NetworkHttpThrottled, UserErrorCode.NetworkProblem },
            { DiagnosticCodes.NetworkHttpServerError, UserErrorCode.NetworkProblem },
            { DiagnosticCodes.NetworkHttpUnexpected, UserErrorCode.ResourceUnavailable },
            { DiagnosticCodes.FileNotFound, UserErrorCode.ResourceUnavailable },
            { DiagnosticCodes.FileAccessDenied, UserErrorCode.LocalAccessProblem },
            { DiagnosticCodes.FileIoFailed, UserErrorCode.LocalAccessProblem },
            { DiagnosticCodes.CacheDiskFull, UserErrorCode.StorageFull },
            { DiagnosticCodes.CacheIdentityConflict, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.CacheIntegrityFailed, UserErrorCode.ResourceDamaged },
            { DiagnosticCodes.CacheIndexFailed, UserErrorCode.LocalAccessProblem },
        };

        public UserErrorCode Map(string diagnosticCode, LoadStage stage, IReadOnlyDictionary<string, object> context)
        {
            return Table.TryGetValue(diagnosticCode, out var code) ? code : UserErrorCode.Unknown;
        }

        internal static UserErrorCode MapStatic(string diagnosticCode, LoadStage stage, IReadOnlyDictionary<string, object> context)
        {
            return Instance.Map(diagnosticCode, stage, context);
        }
    }
}
