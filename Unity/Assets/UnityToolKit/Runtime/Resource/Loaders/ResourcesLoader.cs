/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : Unity Resources 加载器 (P5, §8.1)。地址为 Resources 下相对路径；
 *                Unity API 通过执行上下文在主线程执行；可单独卸载的类型执行 UnloadAsset，
 *                其余 (GameObject/Component) 归还管理引用并交由显式全局回收阶段处理，
 *                不伪造立即回收承诺。取消后的迟到结果被丢弃。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common.Resource;
using UnityEngine;
using Object = UnityEngine.Object;
using ResourceRequest = ToolKit.Tools.Common.Resource.ResourceRequest;

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

            operationToken.ThrowIfCancellationRequested(); // 迟到结果不再交付

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
            return new LoadedAsset(asset, null,
                isAlive: () => asset != null,
                releaseAsync: () =>
                {
                    unload?.Invoke();
                    return Task.CompletedTask;
                });
        }

        private static async Task<Object> _LoadOnMainThread(string address, Type type, CancellationToken ct)
        {
            var request = Resources.LoadAsync(address, type);
            var tcs = new TaskCompletionSource<Object>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<AsyncOperation>? handler = null;
            var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            handler = _ =>
            {
                request.completed -= handler;
                tcs.TrySetResult(request.asset);
            };
            request.completed += handler;
            var result = await tcs.Task.ConfigureAwait(false);
            registration.Dispose();
            return result!;
        }
    }
}
