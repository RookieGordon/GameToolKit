using System;
using System.Collections.Generic;
using System.Threading;

namespace ToolKit.Tools.Common
{
    /// <summary> 
    /// 最终用户可理解的错误类别；本地化文案由业务按此码查询，不写死在框架中 
    /// </summary>
    public enum UserErrorCode
    {
        NetworkProblem,        // 网络问题，请稍后重试
        ResourceUnavailable,   // 资源暂时无法加载
        StorageFull,           // 存储空间不足
        LocalAccessProblem,    // 无法读取或保存本地资源
        ResourceDamaged,       // 资源校验失败，请重试
        OperationUnavailable,  // 当前无法完成此操作
        Unknown,               // 加载失败，请稍后重试
    }

    /// <summary>
    /// 失败发生的阶段；用于诊断定位，不用于业务分支
    /// </summary>
    public enum LoadStage
    {
        ValidateRequest,
        Route,
        Resolve,
        WaitForLoad,
        CacheLookup,
        Download,
        ValidateContent,
        ReadFile,
        Convert,
        LoadAsset,
        Instantiate,
        ReleaseAsset,
        ReturnInstance,
        DestroyInstance,
        Dispose,
    }

    /// <summary> 
    /// 框架内置稳定诊断码；第三方实现使用自有前缀 (如 addressables.*) 扩展 
    /// </summary>
    public static class DiagnosticCodes
    {
        public const string LoaderNotRegistered = "loader.not_registered";
        public const string LoaderResolveFailed = "loader.resolve_failed";
        public const string LoaderInvalidResult = "loader.invalid_result";
        public const string AssetNotFound = "asset.not_found";
        public const string AssetTypeMismatch = "asset.type_mismatch";
        public const string AssetInvalidated = "asset.invalidated";
        public const string AssetLoadFailed = "asset.load_failed";
        // 保留已有诊断字符串，避免日志筛选和外部错误映射随源码命名一起失效。
        public const string AssetConversionFailed = "asset.decode_failed";
        public const string AssetUnsupportedRepresentation = "asset.unsupported_representation";
        public const string RequestTimeout = "request.timeout";
        public const string InstanceUnsupported = "instance.unsupported";
        public const string InstancePoolClosed = "instance.pool_closed";
        public const string InstanceCreateFailed = "instance.create_failed";
        public const string InstanceResetFailed = "instance.reset_failed";
        public const string InstanceDestroyFailed = "instance.destroy_failed";
        public const string LifecycleManagerDisposed = "lifecycle.manager_disposed";
        public const string LifecycleReleaseFailed = "lifecycle.release_failed";
        public const string LifecycleSystemDisposed = "lifecycle.system_disposed";
        public const string ObserverCallbackFailed = "observer.callback_failed";
        public const string InternalUnexpected = "internal.unexpected";
        public const string InternalConsistency = "internal.consistency_error";

        // —— 网络：主动取消不映射为网络故障 ——
        public const string NetworkInvalidUri = "network.invalid_uri";
        public const string NetworkDnsFailed = "network.dns_failed";
        public const string NetworkConnectFailed = "network.connect_failed";
        public const string NetworkTlsFailed = "network.tls_failed";
        public const string NetworkTimeout = "network.timeout";
        public const string NetworkInterrupted = "network.interrupted";
        public const string NetworkHttpNotFound = "network.http_not_found";
        public const string NetworkHttpDenied = "network.http_denied";
        public const string NetworkHttpThrottled = "network.http_throttled";
        public const string NetworkHttpServerError = "network.http_server_error";
        public const string NetworkHttpUnexpected = "network.http_unexpected";

        // —— 本地文件 ——
        public const string FileNotFound = "file.not_found";
        public const string FileAccessDenied = "file.access_denied";
        public const string FileIoFailed = "file.io_failed";

        // —— 磁盘缓存 ——
        public const string CacheDiskFull = "cache.disk_full";
        public const string CacheIdentityConflict = "cache.identity_conflict";
        public const string CacheIntegrityFailed = "cache.integrity_failed";
        public const string CacheIndexFailed = "cache.index_failed";
    }

    /// <summary> 默认映射表；未知码归为 Unknown 并原样保留诊断 </summary>
    public sealed class DefaultErrorMapper
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

    /// <summary>
    /// 不可变的结构化加载错误。UserCode 面向最终用户；DiagnosticCode/Stage/Context/Cause 面向开发者。
    /// 主错误码与原始原因保持不变。
    /// </summary>
    public sealed class ResourceError
    {
        public UserErrorCode UserCode { get; }
        public string DiagnosticCode { get; }
        public LoadStage Stage { get; }
        public string DiagnosticId { get; }
        public IReadOnlyDictionary<string, object> Context { get; }
        public Exception? Cause { get; }

        public ResourceError(
            string diagnosticCode,
            LoadStage stage,
            Exception? cause = null,
            IReadOnlyDictionary<string, object>? context = null,
            UserErrorCode? userCode = null,
            string? diagnosticId = null)
        {
            DiagnosticCode = diagnosticCode ?? DiagnosticCodes.InternalUnexpected;
            Stage = stage;
            Cause = cause;
            Context = context ?? EmptyContext;
            UserCode = userCode ?? DefaultErrorMapper.MapStatic(DiagnosticCode, stage, Context);
            DiagnosticId = diagnosticId ?? NewDiagnosticId();
        }

        /// <summary> 应用注入的用户码映射；诊断证据与 DiagnosticId 保持不变 </summary>
        public ResourceError WithUserCode(UserErrorCode userCode)
        {
            if (userCode == UserCode)
            {
                return this;
            }
            return new ResourceError(DiagnosticCode, Stage, Cause,
                Context, userCode, DiagnosticId);
        }

        public override string ToString()
        {
            return $"{DiagnosticCode}@{Stage} (user={UserCode}, id={DiagnosticId})";
        }

        private static IReadOnlyDictionary<string, object> EmptyContext { get; } =
            new Dictionary<string, object>();

        private static IReadOnlyList<ResourceError> EmptyRelated { get; } = new ResourceError[0];

        private static long _diagnosticSeed;

        private static string NewDiagnosticId()
        {
            return "diag-" + Interlocked.Increment(ref _diagnosticSeed).ToString();
        }
    }


    public sealed class ResourceException : Exception
    {
        public ResourceError ResourceError;

        public ResourceException(ResourceError error): base(BuildMessage(error), error.Cause)
        {
            ResourceError = error;
        }

        /// <summary> 常用构造：诊断码 + 阶段 + 资源键 (+ 原因) </summary>
        public static ResourceException Of(string diagnosticCode, LoadStage stage, ResourceKey key, Exception cause = null)
        {
            return new ResourceException(new ResourceError(diagnosticCode, stage, cause,
                new Dictionary<string, object> { { "key", key.ToString() } }));
        }

        private static string BuildMessage(ResourceError error)
        {
            var ctx = error.Context.Count > 0 ? " context=[" + string.Join(";", error.Context.Keys) + "]" : "";
            return $"{error.DiagnosticCode} at {error.Stage} (user={error.UserCode}, id={error.DiagnosticId}){ctx}";
        }
    }
}
