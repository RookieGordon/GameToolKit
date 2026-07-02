using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using ToolKit.Tools.Common;
using ToolKit.Tools.Extension;
using UnityEngine;
using UnityToolKit.Runtime.Common;

namespace UnityToolKit.Runtime.Resource
{
    public class UResourceManager: UnitySingleton<UResourceManager>
    {
        private ResourceManager _resourceManager;

        private ResourceBinder _resourceBinder;

        private void Awake()
        {
            _resourceManager = new ResourceManager();
            _resourceManager.RegisterLoader(new AssetBundleLoader());
            _resourceManager.RegisterLoader(new ResourcesLoader());
            _resourceManager.RegisterInstancer(new GameObjectInstanceProvider());
            _resourceBinder = new ResourceBinder(_resourceManager);
        }

        #region 协程加载API

        public void LoadAssetAsync(string address, Action<ResourceRef> onLoaded = null, Action<LoadError> onLoadError = null, CancellationToken cancellationToken = default)
        {
            StartCoroutine(LoadAssetAsyncInner(address, ELoadType.AssetBundle, onLoaded, onLoadError, cancellationToken));
        }
        
        public void LoadResourceAsync(string address, Action<ResourceRef> onLoaded = null, Action<LoadError> onLoadError = null, CancellationToken cancellationToken = default)
        {
            StartCoroutine(LoadAssetAsyncInner(address, ELoadType.Resources, onLoaded, onLoadError, cancellationToken));
        }
        
        public void LoadLocalFileAsync(string address, Action<ResourceRef> onLoaded = null, Action<LoadError> onLoadError = null, CancellationToken cancellationToken = default)
        {
            StartCoroutine(LoadAssetAsyncInner(address, ELoadType.LocalFile, onLoaded, onLoadError, cancellationToken));
        }
        
        public void LoadRemoteFileAsync(string address, Action<ResourceRef> onLoaded = null, Action<LoadError> onLoadError = null, CancellationToken cancellationToken = default)
        {
            StartCoroutine(LoadAssetAsyncInner(address, ELoadType.RemoteFile, onLoaded, onLoadError, cancellationToken));
        }
        
        public void ApplyResourceAsync<TTarget, TResource>(TTarget target, string address, IApplicable applicable, Action onFinished, CancellationToken cancellationToken = default, params System.Object[] applyArgs) where TTarget : UnityEngine.Object where TResource : UnityEngine.Object
        {
            StartCoroutine(ApplyAsyncInner<TTarget, TResource>(target, address, applicable, ELoadType.Resources, onFinished, cancellationToken, applyArgs));
        }
        
        public void ApplyAssetAsync<TTarget, TResource>(TTarget target, string address, IApplicable applicable, Action onFinished, CancellationToken cancellationToken = default, params System.Object[] applyArgs) where TTarget : UnityEngine.Object where TResource : UnityEngine.Object
        {
            StartCoroutine(ApplyAsyncInner<TTarget, TResource>(target, address, applicable, ELoadType.AssetBundle, onFinished, cancellationToken, applyArgs));
        }
        
        public void ApplyLocalFileAsync<TTarget, TResource>(TTarget target, string address, IApplicable applicable, Action onFinished, CancellationToken cancellationToken = default, params System.Object[] applyArgs) where TTarget : UnityEngine.Object where TResource : UnityEngine.Object
        {
            StartCoroutine(ApplyAsyncInner<TTarget, TResource>(target, address, applicable, ELoadType.LocalFile, onFinished, cancellationToken, applyArgs));
        }
        
        public void ApplyRemoteFileAsync<TTarget, TResource>(TTarget target, string address, IApplicable applicable, Action onFinished, CancellationToken cancellationToken = default, params System.Object[] applyArgs) where TTarget : UnityEngine.Object where TResource : UnityEngine.Object
        {
            StartCoroutine(ApplyAsyncInner<TTarget, TResource>(target, address, applicable, ELoadType.Resources, onFinished, cancellationToken, applyArgs));
        }

        public void RevertAsset<T>(T target, IApplicable applicable) where T : UnityEngine.Object
        {
            _resourceBinder.Revert<T>(target, applicable);
        }
        
        #endregion

        private IEnumerator LoadAssetAsyncInner(string address, ELoadType loadType, Action<ResourceRef> onLoaded, Action<LoadError> onLoadError, CancellationToken cancellationToken)
        {
            var task = _resourceManager.LoadRefAsync(address, loadType, cancellationToken);
            yield return new WaitUntil(() => task.IsCompleted || task.IsFaulted);
            var result = task.Result;
            var isFailed = task.IsFaulted || result.Error.Code != ELoadError.None;
            if (isFailed)
            {
                if (result.Error.Code == ELoadError.None)
                {
                    onLoadError?.Invoke(new LoadError(ELoadError.Unknown, task.Exception?.Message, task.Exception));
                }
                else
                {
                    onLoadError?.Invoke(result.Error);
                }
            }
            else
            {
                onLoaded?.Invoke(result);
            }
        }

        private IEnumerator ApplyAsyncInner<TTarget, TResource>(TTarget target, string address, IApplicable applicable,
            ELoadType loadType, Action onFinished, CancellationToken cancellationToken, params System.Object[] applyArgs) where TTarget : class where TResource : class
        {
            var task = _resourceBinder.ApplyAsync<TTarget, TResource>(target, address, applicable, loadType, cancellationToken, applyArgs);
            yield return new WaitUntil(() => task.IsCompleted || task.IsFaulted);
            if (task.IsCompleted)
            {
                onFinished?.Invoke();
            }
        }
        
    }
}
