using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using ToolKit.Tools.Extension;
using UnityEngine;
using UnityToolKit.Runtime.Common;
using UnityToolKit.Runtime.Utility;

namespace UnityToolKit.Runtime.Resource
{
    public class GResourceManager: UnitySingleton<GResourceManager>
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

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
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
        
        public void LoadAssetGameObjAsync(string address, Action<ResourceRef> onLoaded = null, Action<LoadError> onLoadError = null, CancellationToken cancellationToken = default)
        {
            StartCoroutine(LoadGameObjectAsyncInner(address, ELoadType.AssetBundle, onLoaded, onLoadError, cancellationToken));
        }
        
        public void LoadResourceGameObjAsync(string address, Action<ResourceRef> onLoaded = null, Action<LoadError> onLoadError = null, CancellationToken cancellationToken = default)
        {
            StartCoroutine(LoadGameObjectAsyncInner(address, ELoadType.Resources, onLoaded, onLoadError, cancellationToken));
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
            ResourceBindingAutoRevert.Unregister(target, applicable);
            _resourceBinder.Revert<T>(target, applicable);
        }
        
        #endregion

        #region AssetBundle预加载

        

        #endregion

        private IEnumerator LoadAssetAsyncInner(string address, ELoadType loadType, Action<ResourceRef> onLoaded, Action<LoadError> onLoadError, CancellationToken cancellationToken)
        {
            var task = _resourceManager.LoadRefAsync(address, loadType, cancellationToken);
            yield return TaskToCoroutineUtil.WaitForTask(task);
            if (task.IsFaulted)
            {
                onLoadError?.Invoke(new LoadError(ELoadError.Unknown, task.Exception?.Message, task.Exception));
                yield break;
            }

            if (task.IsCanceled)
            {
                onLoadError?.Invoke(new LoadError(ELoadError.Cancelled, $"加载已取消: {address}"));
                yield break;
            }

            var result = task.Result;
            var isFailed = result.Error.Code != ELoadError.None;
            if (isFailed)
            {
                onLoadError?.Invoke(result.Error);
            }
            else
            {
                onLoaded?.Invoke(result);
            }
        }

        private IEnumerator ApplyAsyncInner<TTarget, TResource>(TTarget target, string address, IApplicable applicable,
            ELoadType loadType, Action onFinished, CancellationToken cancellationToken, params System.Object[] applyArgs)
            where TTarget : UnityEngine.Object where TResource : UnityEngine.Object
        {
            ResourceBindingAutoRevert.Register(target, applicable, () => RevertAsset(target, applicable));
            var task = _resourceBinder.ApplyAsync<TTarget, TResource>(target, address, applicable, loadType, cancellationToken, applyArgs);
            yield return TaskToCoroutineUtil.WaitForTask(task);
            if (task.IsFaulted || task.IsCanceled)
            {
                ResourceBindingAutoRevert.Unregister(target, applicable);
            }
            if (task.IsCompleted)
            {
                onFinished?.Invoke();
            }
        }
        
        private IEnumerator LoadGameObjectAsyncInner(string address, ELoadType loadType, Action<ResourceRef> onLoaded, Action<LoadError> onLoadError, CancellationToken cancellationToken)
        {
            var task = _resourceManager.InstantiateRefAsync(address, loadType, cancellationToken);
            yield return TaskToCoroutineUtil.WaitForTask(task);
            if (task.IsFaulted)
            {
                onLoadError?.Invoke(new LoadError(ELoadError.Unknown, task.Exception?.Message, task.Exception));
                yield break;
            }

            if (task.IsCanceled)
            {
                onLoadError?.Invoke(new LoadError(ELoadError.Cancelled, $"加载已取消: {address}"));
                yield break;
            }

            var result = task.Result;
            var isFailed = result.Error.Code != ELoadError.None;
            if (isFailed)
            {
                onLoadError?.Invoke(result.Error);
            }
            else
            {
                BindInstanceAutoRelease(result);
                onLoaded?.Invoke(result);
            }
        }

        internal static void TryDisposeAfterUnityDestroy(ResourceRef resourceRef, long token)
        {
            if (_instance == null)
            {
                return;
            }

            _instance.DisposeAfterUnityDestroy(resourceRef, token);
        }

        private void DisposeAfterUnityDestroy(ResourceRef resourceRef, long token)
        {
            if (resourceRef == null || token == 0)
            {
                return;
            }

            StartCoroutine(DisposeAfterUnityDestroyInner(resourceRef, token));
        }

        private IEnumerator DisposeAfterUnityDestroyInner(ResourceRef resourceRef, long token)
        {
            yield return null;

            if (resourceRef != null && resourceRef.Token == token)
            {
                resourceRef.Dispose();
            }
        }

        private static void BindInstanceAutoRelease(ResourceRef resourceRef)
        {
            var instance = resourceRef.GetGameObject();
            if (instance == null)
            {
                return;
            }

            var autoRelease = instance.GetComponent<ResourceRefAutoRelease>();
            if (autoRelease == null)
            {
                autoRelease = instance.AddComponent<ResourceRefAutoRelease>();
            }
            autoRelease.Bind(resourceRef);
        }
    }
}
