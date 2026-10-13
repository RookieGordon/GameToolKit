/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : AssetBundle 资产加载适配器。包的打开、下载、依赖和卸载由接入方管理；
 *                本类只取得已就绪的包并加载包内资产，不接管包或资产的释放。
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
    /// <summary>
    /// 本适配器的地址结果；不打开或持有包。GetBundleAsync 在实际加载时由外部所有者提供已就绪的包。
    /// 包与依赖的共享、取消、卸载，以及资产使用期间的有效性均由外部所有者管理。
    /// </summary>
    public sealed class BundleLocation
    {
        public readonly string ContainerId;
        public readonly string Version;
        public readonly string AssetName;
        public readonly Func<CancellationToken, Task<AssetBundle>> GetBundleAsync;

        public BundleLocation(string containerId, string assetName,
            Func<CancellationToken, Task<AssetBundle>> getBundleAsync, string version = "")
        {
            ContainerId = containerId;
            AssetName = assetName;
            GetBundleAsync = getBundleAsync ?? throw new ArgumentNullException(nameof(getBundleAsync));
            Version = version;
        }
    }

    public sealed class SimpleAssetBundleLoader : IResourceLoader
    {
        private readonly IExecutionContext _context;
        private readonly Func<ResourceRequest, CancellationToken, Task<BundleLocation>> _locator;

        public SimpleAssetBundleLoader(IExecutionContext context,
            Func<ResourceRequest, CancellationToken, Task<BundleLocation>> locator)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        }

        public async Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = await _locator(request, cancellationToken).ConfigureAwait(false);
            if (location == null || string.IsNullOrEmpty(location.ContainerId)
                || string.IsNullOrEmpty(location.AssetName))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "address", request.Address } }));
            }
            var localKey = string.Join("|", "bundle", location.ContainerId, location.Version, location.AssetName,
                request.RequestedType.FullName ?? request.RequestedType.Name);
            return new ResolvedResource(localKey, request.RequestedType, location);
        }

        public async Task<LoadedAsset> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            var location = (BundleLocation)resource.Payload!;
            operationToken.ThrowIfCancellationRequested();
            var bundle = await location.GetBundleAsync(operationToken).ConfigureAwait(false);
            var asset = await _context.InvokeAsync(() =>
            {
                operationToken.ThrowIfCancellationRequested();
                if (bundle == null)
                {
                    throw _NotFound(location, "bundle-unavailable");
                }
                var request = bundle.LoadAssetAsync(location.AssetName, resource.RepresentationType);
                // 已启动的 Unity 请求不可中断。观察真实完成；迟到资产仍归外部包所有者管理。
                return UnityAsyncOperationAwaiter.ObserveAsync<Object>(
                    request, () => request.asset);
            }).ConfigureAwait(false);
            if (await _context.RunAsync(() => asset == null).ConfigureAwait(false))
            {
                throw _NotFound(location, "asset-unavailable");
            }
            // 业务卸载仅移除框架记录，不 Destroy 资产，也不 Unload 包。
            return new LoadedAsset(asset!, isAlive: () => asset != null);
        }

        private static ResourceLoadException _NotFound(BundleLocation location, string reason)
        {
            return new ResourceLoadException(new LoadError(
                DiagnosticCodes.AssetNotFound, LoadStage.LoadAsset, CleanupStatus.Complete, null,
                new Dictionary<string, object>
                {
                    { "container", location.ContainerId },
                    { "asset", location.AssetName },
                    { "reason", reason },
                }));
        }
    }
}
