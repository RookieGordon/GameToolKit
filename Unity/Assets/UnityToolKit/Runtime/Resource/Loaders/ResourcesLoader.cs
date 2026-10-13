/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : Unity Resources 加载器。只卸载 Unity 支持单独卸载的资产；
 *                GameObject/Component 资产移除管理引用后，由业务选择全局回收时机。
 *                已启动的请求观察到真实完成，取消后的产物也交给核心统一释放或隔离。
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
            cancellationToken.ThrowIfCancellationRequested();
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

        public async Task<LoadedAsset> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            var address = (string)resource.Payload!;
            Object? asset;
            try
            {
                asset = await _context.InvokeAsync(() =>
                {
                    operationToken.ThrowIfCancellationRequested();
                    var request = Resources.LoadAsync(address, resource.RepresentationType);
                    // 此后等待真实完成，即使业务已经取消。核心负责无人接收的产物，
                    // 因而 UnloadAsset 失败时仍能保留实际对象，不会被观察器吞掉。
                    return UnityAsyncOperationAwaiter.ObserveAsync<Object>(
                        request, () => request.asset);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
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

            if (await _context.RunAsync(() => asset == null).ConfigureAwait(false))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetNotFound, LoadStage.LoadAsset, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "address", address } }));
            }
            return new LoadedAsset(asset!,
                isAlive: () => asset != null,
                releaseAsync: () => _context.RunAsync(() =>
                {
                    if (asset is not GameObject && asset is not Component && asset != null)
                    {
                        Resources.UnloadAsset(asset);
                    }
                }));
        }
    }
}
