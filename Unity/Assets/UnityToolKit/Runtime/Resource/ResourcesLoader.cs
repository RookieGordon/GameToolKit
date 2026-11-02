/*
 * author       : Gordon
 * datetime     : 2026-10-15 (2026-11-02 按新契约重写)
 * description  : 内置 Unity Resources 来源加载器 (需求 R01)。
 *                在执行上下文 (主线程) 内发起 Resources.LoadAsync，并轮询到完成；观察本轮取消。
 *                Resources 资产由引擎共享持有：默认不交出释放责任 (Release 为空)，核心因此不推断自己拥有底层对象；
 *                项目需要逐资产回收时打开 unloadAssetOnRelease，对非 GameObject 资产调用 Resources.UnloadAsset。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityToolKit.Runtime.Resource
{
    public sealed class ResourcesLoader : ILoader
    {
        private readonly IExecutionContext _context;
        private readonly bool _unloadAssetOnRelease;

        /// <param name="context">主线程执行上下文；引擎 API 只能在其中调用</param>
        /// <param name="unloadAssetOnRelease">卸载时是否对非 GameObject/Component 资产调用 Resources.UnloadAsset</param>
        public ResourcesLoader(IExecutionContext context, bool unloadAssetOnRelease = false)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _unloadAssetOnRelease = unloadAssetOnRelease;
        }

        public Task<IResource> LoadAsync(ResourceKey key, IProgress<OperationProgress> progress, CancellationToken cancellation)
        {
            var assetType = typeof(Object).IsAssignableFrom(key.RequestedType) ? key.RequestedType : typeof(Object);
            return _context.InvokeAsync<IResource>(async () =>
            {
                cancellation.ThrowIfCancellationRequested();
                var request = Resources.LoadAsync(key.Address, assetType);
                while (!request.isDone)
                {
                    cancellation.ThrowIfCancellationRequested();
                    progress?.Report(new OperationProgress(Mathf.Clamp01(request.progress), "Resources"));
                    await Task.Yield();                                   // 回到主线程的 SynchronizationContext
                }

                var asset = request.asset;
                if (asset == null) throw ResourceException.Of(DiagnosticCodes.AssetNotFound, LoadStage.LoadAsset, key);
                return new Product(asset, _ReleaseOf(asset));
            });
        }

        private Action _ReleaseOf(Object asset)
        {
            if (!_unloadAssetOnRelease || asset is GameObject || asset is Component) return null;
            return () => Resources.UnloadAsset(asset);
        }
    }
}
