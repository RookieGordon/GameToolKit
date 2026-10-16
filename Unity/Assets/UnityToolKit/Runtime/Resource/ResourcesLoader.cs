/*
 * author       : Gordon
 * datetime     : 2026-10-15
 * description  : 内置 Unity Resources 来源加载器 (需求 R01)。ResolveAsync 只回答身份，
 *                LoadAsync 通过适配后的状态串行域调用 Resources.LoadAsync。
 *                Resources 资产由引擎共享持有，本加载器不提供逐引用释放回调：
 *                框架因此不推断自己拥有其底层对象，也不擅自 UnloadAsset。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;
using Object = UnityEngine.Object;
// UnityEngine 也有 ResourceRequest：这里明确取资源系统的那一个，避免与引擎类型歧义。
using ResourceRequest = ToolKit.Tools.Common.ResourceRequest;

namespace UnityToolKit.Runtime.Resource
{
    public sealed class ResourcesLoader : IResourceLoader
    {
        private readonly UnityExecutionContext _context;

        /// <param name="context">主线程状态串行域；引擎 API 只能在其中调用</param>
        public ResourcesLoader(UnityExecutionContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// 身份 = 地址 + 目标类型。Resources 的路径本身就是它的身份，
        /// 不引入版本或变体：需要版本时由业务改用可提供 revision 的来源。
        /// </summary>
        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(request.Address))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "reason", "empty-address" } }));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var localKey = string.Join("|", "resources", request.Address, _TypeKey(request.RequestedType));
            return Task.FromResult(new ResolvedResource(localKey, request.RequestedType,
                new ResourcesPayload(request.Address)));
        }

        public Task<LoadedContent> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            if (resource.Payload is not ResourcesPayload payload)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderResolveFailed, LoadStage.LoadAsset, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "reason", "Payload 不是 ResourcesPayload" } }));
            }

            // Resources.LoadAsync 只能在主线程发起；其余阶段保持异步等待。
            return _context.InvokeAsync(async () =>
            {
                operationToken.ThrowIfCancellationRequested();
                var request = Resources.LoadAsync(payload.Path, resource.RepresentationType);
                while (!request.isDone)
                {
                    operationToken.ThrowIfCancellationRequested();
                    progress?.Report(new ResourceProgress(LoadStage.LoadAsset, 0, null));
                    await Task.Yield();
                }

                var asset = request.asset;
                if (asset == null)
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.AssetNotFound, LoadStage.LoadAsset, CleanupStatus.Complete, null,
                        new Dictionary<string, object> { { "key", resource.LocalKey } }));
                }
                if (resource.RepresentationType != null
                    && !resource.RepresentationType.IsInstanceOfType(asset))
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.AssetTypeMismatch, LoadStage.LoadAsset, CleanupStatus.Complete, null,
                        new Dictionary<string, object>
                        {
                            { "key", resource.LocalKey },
                            { "requestedType", resource.RepresentationType.Name },
                            { "actualType", asset.GetType().Name },
                        }));
                }

                // 引擎共享资产：加载器交出内容即最终业务对象，无需转换步骤。
                // 存活按引擎语义；不提供释放回调——框架因此不推断自己拥有其底层对象。
                return new LoadedContent(asset);
            });
        }

        private static string _TypeKey(Type type) =>
            type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

        /// <summary> 解析结果只携带引擎路径：身份串不参与加载 </summary>
        private sealed class ResourcesPayload
        {
            internal ResourcesPayload(string path)
            {
                Path = path;
            }

            internal string Path { get; }
        }
    }
}
