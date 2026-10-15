/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 错误契约。用户码 (UserErrorCode，面向最终用户) + 稳定诊断码 (DiagnosticCodes 字符串
 *                常量) + 阶段 (LoadStage) + 清理状态 (CleanupStatus) 构成不可变 LoadError；
 *                资源操作失败统一抛 ResourceLoadException，主动取消抛 OperationCanceledException。
 *                诊断码使用稳定字符串而非封闭枚举，用户加载器可用自有前缀扩展。
 *                错误映射表 (IErrorMapper/DefaultErrorMapper) 含执行逻辑，放在接入层 ResourceSystemSetup.cs。
 */

using System;
using System.Collections.Generic;
using System.Threading;

namespace ToolKit.Tools.Common
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

    /// <summary> 本次操作已取得但尚未交付的所有权是否完成回退 </summary>
    public enum CleanupStatus
    {
        Unknown = 0,
        Complete = 1,
        Incomplete = 2,
    }

    /// <summary> 框架内置稳定诊断码；第三方实现使用自有前缀 (如 addressables.*) 扩展 </summary>
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

    /// <summary>
    /// 不可变的结构化加载错误。UserCode 面向最终用户；DiagnosticCode/Stage/Context/Cause 面向开发者。
    /// Cleanup 描述失败时已取得所有权的回退状态；RelatedErrors 保存回退过程的关联失败，
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
            CleanupStatus cleanup = CleanupStatus.Unknown, // 保守默认：未确认清理状态不得承诺 Complete
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
            UserCode = userCode ?? DefaultErrorMapper.MapStatic(DiagnosticCode, stage, Context);
            DiagnosticId = diagnosticId ?? _NewDiagnosticId();
        }

        /// <summary>
        /// 携带关联失败创建副本 (回退失败叠加，不覆盖主错误)。
        /// 关联回退未确认完成时，总体清理状态升级，不让主错误继续承诺 Complete。
        /// </summary>
        public LoadError WithRelated(LoadError related)
        {
            var list = new List<LoadError>(RelatedErrors.Count + 1);
            list.AddRange(RelatedErrors);
            list.Add(related);
            var cleanup = Cleanup;
            if (related.Cleanup != CleanupStatus.Complete)
            {
                if (cleanup == CleanupStatus.Complete)
                {
                    cleanup = related.Cleanup;
                }
                else if (related.Cleanup == CleanupStatus.Unknown)
                {
                    cleanup = CleanupStatus.Unknown;
                }
            }
            return new LoadError(DiagnosticCode, Stage, cleanup, Cause,
                Context, list, UserCode, DiagnosticId);
        }

        /// <summary> 应用注入的用户码映射；诊断证据与 DiagnosticId 保持不变 </summary>
        public LoadError WithUserCode(UserErrorCode userCode)
        {
            if (userCode == UserCode)
            {
                return this;
            }
            return new LoadError(DiagnosticCode, Stage, Cleanup, Cause,
                Context, RelatedErrors, userCode, DiagnosticId);
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

        /// <summary> 本次回退失败的实际对象，只用于隔离，不安排自动重试。 </summary>
        public IReadOnlyList<object> CleanupRemainders { get; }

        public ResourceLoadException(LoadError error, IReadOnlyList<object>? cleanupRemainders = null)
            : base(_BuildMessage(error), error.Cause)
        {
            Error = error;
            CleanupRemainders = cleanupRemainders == null
                ? Array.Empty<object>() : new List<object>(cleanupRemainders).AsReadOnly();
        }

        private static string _BuildMessage(LoadError error)
        {
            var ctx = error.Context.Count > 0 ? " context=[" + string.Join(";", error.Context.Keys) + "]" : "";
            return $"{error.DiagnosticCode} at {error.Stage} (user={error.UserCode}, cleanup={error.Cleanup}, id={error.DiagnosticId}){ctx}";
        }
    }
}
