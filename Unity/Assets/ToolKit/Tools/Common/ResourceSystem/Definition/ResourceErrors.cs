/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 两层错误契约 (P0)。用户码 (UserErrorCode) + 稳定诊断码 (DiagnosticCodes 字符串常量)
 *                + 阶段 (LoadStage) + 清理状态 (CleanupStatus) 构成不可变 LoadError；
 *                资源操作失败统一抛 ResourceLoadException；主动取消抛 OperationCanceledException。
 *                诊断码使用稳定字符串而非封闭枚举，用户加载器可用自有前缀扩展。
 */

using System;
using System.Collections.Generic;
using System.Threading;

namespace ToolKit.Tools.Common.Resource
{
    /// <summary> 最终用户可理解的错误类别；本地化文案由业务按此码查询，不写死在框架中 </summary>
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

    /// <summary> 失败发生的阶段；用于诊断定位，不用于业务分支 </summary>
    public enum LoadStage
    {
        ValidateRequest,
        Route,
        Resolve,
        WaitForLoad,
        CacheLookup,
        ReserveCapacity,
        Download,
        ValidateContent,
        ReadFile,
        Decode,
        LoadAsset,
        Instantiate,
        ApplyBinding,
        ReleaseAsset,
        ReturnInstance,
        DestroyInstance,
        CacheEvict,
        Shutdown,
    }

    /// <summary> 本次操作已取得但尚未交付的所有权是否完成回退 </summary>
    public enum CleanupStatus
    {
        Unknown = 0,
        Complete = 1,
        Incomplete = 2,
    }

    /// <summary> 框架内置稳定诊断码常量；第三方实现使用自有前缀 (如 addressables.*) 扩展 </summary>
    public static class DiagnosticCodes
    {
        public const string LoaderNotRegistered = "loader.not_registered";
        public const string LoaderResolveFailed = "loader.resolve_failed";
        public const string LoaderInvalidResult = "loader.invalid_result";
        public const string AssetNotFound = "asset.not_found";
        public const string AssetTypeMismatch = "asset.type_mismatch";
        public const string AssetInvalidated = "asset.invalidated";
        public const string AssetLoadFailed = "asset.load_failed";
        public const string AssetDecodeFailed = "asset.decode_failed";
        public const string AssetUnsupportedRepresentation = "asset.unsupported_representation";
        public const string RequestTimeout = "request.timeout";
        public const string InstanceUnsupported = "instance.unsupported";
        public const string InstancePoolClosed = "instance.pool_closed";
        public const string InstanceCreateFailed = "instance.create_failed";
        public const string InstanceResetFailed = "instance.reset_failed";
        public const string InstanceDestroyFailed = "instance.destroy_failed";
        public const string LifecycleManagerClosing = "lifecycle.manager_closing";
        public const string LifecycleReleaseFailed = "lifecycle.release_failed";
        public const string LifecycleOutstandingOwners = "lifecycle.outstanding_owners";
        public const string LifecycleSystemClosing = "lifecycle.system_closing";
        public const string ObserverCallbackFailed = "observer.callback_failed";
        public const string InternalUnexpected = "internal.unexpected";
        public const string InternalConsistency = "internal.consistency_error";

        // —— 网络 (§10.8)：主动取消不映射为网络故障 ——
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
        public const string NetworkProtocolError = "network.protocol_error";

        // —— 本地文件 ——
        public const string FileNotFound = "file.not_found";
        public const string FileAccessDenied = "file.access_denied";
        public const string FileIoFailed = "file.io_failed";

        // —— 磁盘缓存 ——
        public const string CacheCapacityExceeded = "cache.capacity_exceeded";
        public const string CacheDiskFull = "cache.disk_full";
        public const string CacheEntryTooLarge = "cache.entry_too_large";
        public const string CachePathInvalid = "cache.path_invalid";
        public const string CacheIdentityConflict = "cache.identity_conflict";
        public const string CacheRootInUse = "cache.root_in_use";
        public const string CacheIntegrityFailed = "cache.integrity_failed";
        public const string CacheIndexFailed = "cache.index_failed";
        public const string CacheCommitFailed = "cache.commit_failed";
        public const string CacheDeleteFailed = "cache.delete_failed";
    }

    /// <summary>
    /// 不可变的结构化加载错误。UserCode 面向最终用户；DiagnosticCode/Stage/Context/Cause
    /// 面向开发者。Cleanup 描述失败时已取得所有权的回退状态；RelatedErrors 保存回退过程的关联失败，
    /// 主错误码与原始原因保持不变。
    /// </summary>
    public sealed class LoadError
    {
        public UserErrorCode UserCode { get; }
        public string DiagnosticCode { get; }
        public LoadStage Stage { get; }
        public string DiagnosticId { get; }
        public IReadOnlyDictionary<string, object> Context { get; }
        public Exception? Cause { get; }
        public CleanupStatus Cleanup { get; }
        public IReadOnlyList<LoadError> RelatedErrors { get; }

        public LoadError(
            string diagnosticCode,
            LoadStage stage,
            CleanupStatus cleanup = CleanupStatus.Complete,
            Exception? cause = null,
            IReadOnlyDictionary<string, object>? context = null,
            IReadOnlyList<LoadError>? relatedErrors = null,
            UserErrorCode? userCode = null,
            string? diagnosticId = null)
        {
            DiagnosticCode = diagnosticCode ?? DiagnosticCodes.InternalUnexpected;
            Stage = stage;
            Cleanup = cleanup;
            Cause = cause;
            Context = context ?? EmptyContext;
            RelatedErrors = relatedErrors ?? EmptyRelated;
            UserCode = userCode ?? DefaultErrorMapper.MapStatic(diagnosticCode, stage, Context);
            DiagnosticId = diagnosticId ?? _NewDiagnosticId();
        }

        /// <summary> 携带关联失败创建副本 (回退失败叠加，不覆盖主错误) </summary>
        public LoadError WithRelated(LoadError related)
        {
            var list = new List<LoadError>(RelatedErrors.Count + 1);
            list.AddRange(RelatedErrors);
            list.Add(related);
            return new LoadError(DiagnosticCode, Stage, Cleanup, Cause,
                Context, list, UserCode, DiagnosticId);
        }

        public override string ToString()
        {
            return $"{DiagnosticCode}@{Stage} (user={UserCode}, cleanup={Cleanup}, id={DiagnosticId})";
        }

        private static IReadOnlyDictionary<string, object> EmptyContext { get; } =
            new Dictionary<string, object>();

        private static IReadOnlyList<LoadError> EmptyRelated { get; } = new LoadError[0];

        private static long _diagnosticSeed;

        private static string _NewDiagnosticId()
        {
            return "diag-" + Interlocked.Increment(ref _diagnosticSeed).ToString();
        }
    }

    /// <summary>
    /// 资源操作失败的唯一异常类型。Message 为技术摘要；InnerException = Error.Cause。
    /// 主动取消 (OperationCanceledException) 与参数误用 (ArgumentException 等) 不经过本异常。
    /// </summary>
    public sealed class ResourceLoadException : Exception
    {
        public LoadError Error { get; }

        public ResourceLoadException(LoadError error)
            : base(_BuildMessage(error), error.Cause)
        {
            Error = error;
        }

        private static string _BuildMessage(LoadError error)
        {
            var ctx = error.Context.Count > 0 ? " context=[" + string.Join(";", error.Context.Keys) + "]" : "";
            return $"{error.DiagnosticCode} at {error.Stage} (user={error.UserCode}, cleanup={error.Cleanup}, id={error.DiagnosticId}){ctx}";
        }
    }

    /// <summary> 用户码映射：业务可替换为本地化决策或统一收敛 </summary>
    public interface IErrorMapper
    {
        UserErrorCode Map(string diagnosticCode, LoadStage stage, IReadOnlyDictionary<string, object> context);
    }

    /// <summary> 按第 11.2 节默认表的静态映射；未知码归为 Unknown 并原样保留诊断 </summary>
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
            { DiagnosticCodes.RequestTimeout, UserErrorCode.NetworkProblem },
            { DiagnosticCodes.InstanceUnsupported, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.InstancePoolClosed, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.InstanceCreateFailed, UserErrorCode.ResourceUnavailable },
            { DiagnosticCodes.InstanceResetFailed, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.InstanceDestroyFailed, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.LifecycleManagerClosing, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.LifecycleReleaseFailed, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.LifecycleOutstandingOwners, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.ObserverCallbackFailed, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.InternalUnexpected, UserErrorCode.Unknown },
            { DiagnosticCodes.InternalConsistency, UserErrorCode.Unknown },
            { DiagnosticCodes.AssetDecodeFailed, UserErrorCode.ResourceDamaged },
            { DiagnosticCodes.AssetUnsupportedRepresentation, UserErrorCode.ResourceUnavailable },
            { DiagnosticCodes.LifecycleSystemClosing, UserErrorCode.OperationUnavailable },
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
            { DiagnosticCodes.NetworkProtocolError, UserErrorCode.NetworkProblem },
            { DiagnosticCodes.FileNotFound, UserErrorCode.ResourceUnavailable },
            { DiagnosticCodes.FileAccessDenied, UserErrorCode.LocalAccessProblem },
            { DiagnosticCodes.FileIoFailed, UserErrorCode.LocalAccessProblem },
            { DiagnosticCodes.CacheCapacityExceeded, UserErrorCode.StorageFull },
            { DiagnosticCodes.CacheDiskFull, UserErrorCode.StorageFull },
            { DiagnosticCodes.CacheEntryTooLarge, UserErrorCode.StorageFull },
            { DiagnosticCodes.CachePathInvalid, UserErrorCode.LocalAccessProblem },
            { DiagnosticCodes.CacheIdentityConflict, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.CacheRootInUse, UserErrorCode.OperationUnavailable },
            { DiagnosticCodes.CacheIntegrityFailed, UserErrorCode.ResourceDamaged },
            { DiagnosticCodes.CacheIndexFailed, UserErrorCode.LocalAccessProblem },
            { DiagnosticCodes.CacheCommitFailed, UserErrorCode.LocalAccessProblem },
            { DiagnosticCodes.CacheDeleteFailed, UserErrorCode.LocalAccessProblem },
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
