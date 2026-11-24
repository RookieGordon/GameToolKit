using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityToolKit.Runtime.ResourceSystem
{
    public sealed class ResourcesLoader : ILoader
    {
        private readonly bool _unloadAssetOnRelease;

        /// <param name="unloadAssetOnRelease">卸载时是否对非 GameObject/Component 资产调用 Resources.UnloadAsset</param>
        public ResourcesLoader(bool unloadAssetOnRelease = false)
        {
            _unloadAssetOnRelease = unloadAssetOnRelease;
        }

        /// <summary> 由核心在 Unity 主线程上下文内启动；轮询期间通过 Task.Yield 保持主线程亲和 </summary>
        public async Task<IResource> LoadAsync(ResourceKey key, IProgress<OperationProgress> progress, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var assetType = typeof(Object).IsAssignableFrom(key.RequestedType) ? key.RequestedType : typeof(Object);
            var request = Resources.LoadAsync(key.Address, assetType);
            while (!request.isDone)
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Report(new OperationProgress(Mathf.Clamp01(request.progress), "Resources"));
                await Task.Yield();
            }

            var asset = request.asset;
            if (asset == null) throw ResourceException.Of(DiagnosticCodes.AssetNotFound, LoadStage.LoadAsset, key);
            return new Product(asset, ReleaseOf(asset));
        }

        private Action ReleaseOf(Object asset)
        {
            if (!_unloadAssetOnRelease || asset is GameObject || asset is Component) return null;
            return () => Resources.UnloadAsset(asset);
        }
    }
}
