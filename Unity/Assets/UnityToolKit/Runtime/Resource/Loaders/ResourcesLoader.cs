/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : Unity Resources 加载器 (P5, §8.1)。地址为 Resources 下相对路径；
 *                Unity API 通过执行上下文在主线程执行；可单独卸载的类型执行 UnloadAsset，
 *                其余 (GameObject/Component) 归还管理引用并交由显式全局回收阶段处理，
 *                不伪造立即回收承诺。取消后的迟到结果被观察并回收 (R03)。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;
using Object = UnityEngine.Object;
using ResourceRequest = ToolKit.Tools.Common.ResourceRequest;

namespace UnityToolKit.Runtime.Resource
{
    public sealed class ResourcesLoader : IResourceLoader
    {
        private readonly IExecutionContext _context;

        public ResourcesLoader(IExecutionContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(request.Address))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "reason", "empty-address" } }));
            }
            var localKey = string.Join("|",
                "resources", request.Address, request.RequestedType.FullName ?? request.RequestedType.Name);
            return Task.FromResult(new ResolvedResource(localKey, request.RequestedType, request.Address));
        }

        public Task<LoadedAsset> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            return _LoadCoreAsync((string)resource.Payload!, resource.RepresentationType, operationToken);
        }

        private async Task<LoadedAsset> _LoadCoreAsync(string address, Type type, CancellationToken operationToken)
        {
            Object asset;
            try
            {
                asset = await _context.InvokeAsync(() => _LoadOnMainThread(address, type, operationToken))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // 迟到对象已在观察器内回收
            }
            catch (ResourceLoadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetLoadFailed, LoadStage.LoadAsset, CleanupStatus.Unknown, ex,
                    new Dictionary<string, object> { { "address", address } }));
            }

            Action? unload = null;
            if (asset is not GameObject && asset is not Component)
            {
                unload = () => _context.Invoke(() =>
                {
                    if (asset != null)
                    {
                        Resources.UnloadAsset(asset);
                    }
                });
            }
            // 迟到/排空的成功由上层 (ResourceStore Draining 路径) 通过释放器回收，不在加载器内丢弃
            return new LoadedAsset(asset, null,
                isAlive: () => asset != null,
                releaseAsync: () =>
                {
                    unload?.Invoke();
                    return Task.CompletedTask;
                });
        }

        private static Task<Object?> _LoadOnMainThread(string address, Type type, CancellationToken ct)
        {
            var request = Resources.LoadAsync(address, type);
            return UnityAsyncOperationAwaiter.ObserveAsync<Object>(
                request, () => request.asset,
                disposeLate: late =>
                {
                    // 取消后的迟到结果：可单独卸载的类型立即回收；
                    // GameObject/Component 资产无法单独卸载 (与正常卸载策略一致)
                    if (late is GameObject || late is Component)
                    {
                        return;
                    }
                    if (late != null)
                    {
                        Resources.UnloadAsset(late);
                    }
                }, ct);
        }
    }
}
