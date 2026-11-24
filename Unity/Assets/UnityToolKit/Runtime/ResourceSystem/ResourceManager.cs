using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityToolKit.Runtime.Common;
using UnityToolKit.Runtime.Utility;

namespace UnityToolKit.Runtime.ResourceSystem
{
    public partial class ResourceManager : UnitySingleton<ResourceManager>
    {
        private ToolKit.Tools.Common.ResourceSystem _resSystem;
        private Task _shutdown;

        private const string ConfigPath = "ResourceSystemConfig";

        public bool IsInitialized => _resSystem != null && !_resSystem.IsDisposed && _resSystem.IsInitialized;

        private ToolKit.Tools.Common.ResourceSystem ReadySystem =>
            IsInitialized ? _resSystem : throw NotInitialized();

        /// <summary> 启动初始化并由协程观察错误；需要等待完成后注册加载器时，请 await InitializeAsync。 </summary>
        public void Initialize()
        {
            StartCoroutine(InitializeAsync().ToCoroutine());
        }

        /// <summary>
        /// 在 Unity 主线程首次装配并等待准备工作完成。加载器列表可以为空。
        /// 重复调用复用同一系统和初始化任务；失败或取消后可重试，不重建已装配的对象。
        /// </summary>
        public Task InitializeAsync(CancellationToken token = default)
        {
            if (_shutdown != null) throw new ObjectDisposedException(nameof(ResourceManager));
            if (_resSystem == null)
            {
                var config = Resources.Load<ResourceSystemConfig>(ConfigPath);
                if (config == null)
                    throw new InvalidOperationException("缺少 Resources/ResourceSystemConfig.asset，无法初始化资源系统。");
                _resSystem = new ToolKit.Tools.Common.ResourceSystem(config.ConvertToOptions());
            }
            return _resSystem.InitializeAsync(token);
        }

        /// <summary> 在主线程关闭系统及配置自动创建的服务；外部注册的服务仍由业务关闭。 </summary>
        public Task ShutdownAsync()
            => _shutdown ?? (_shutdown = _resSystem == null ? Task.CompletedTask : _resSystem.DisposeAsync().AsTask());

        private async void OnDestroy()
        {
            try { await ShutdownAsync(); }
            catch (Exception error) { Debug.LogException(error); }
        }

        /// <summary>
        /// 初始化成功后注册或替换一个来源的加载器；外部系统就绪后可随时接入。
        /// 省略策略时沿用配置资产的来源策略；未配置来源用默认值，显式策略仅覆盖本次注册。
        /// </summary>
        public void RegisterLoader(ResourceSource source, ILoader loader, LoaderPolicy? policy = null)
            => ReadySystem.RegisterLoader(source, loader, policy);

        /// <summary> 独立 AssetBundle 系统就绪后接入；等价于注册 AssetBundle 来源。 </summary>
        public void RegisterAssetBundleLoader(ILoader loader, LoaderPolicy? policy = null)
            => RegisterLoader(ResourceSource.AssetBundle, loader, policy);

        #region 异步API

        public Task<ResourceRef<T>> LoadAsync<T>(ResourceSource source, string address,
            IProgress<OperationProgress> progress = null, CancellationToken token = default, object parameters = null)
        {
            return IsInitialized
                ? _resSystem.LoadAsync<T>(source, address, progress, token, parameters)
                : Task.FromException<ResourceRef<T>>(NotInitialized());
        }

        public Task<InstanceLease<T>> RentAsync<T>(ResourceSource source, string address,
            CancellationToken token = default, object parameters = null)
            => ReadySystem.RentAsync<T>(source, address, token, parameters);

        public Task<UnloadResult> UnloadAsync(Func<ResourceKey, bool> selector = null)
            => ReadySystem.UnloadAsync(selector);

        #endregion

        #region 协程API

        public IEnumerator LoadAsync<T>(ResourceSource source, string address,
            IProgress<OperationProgress> progress = null,
            Action<ResourceRef<T>> onCompleted = null,
            Action<Exception> onError = null,
            Action onCancelled = null,
            CancellationToken token = default)
            => ObserveCoroutine(LoadAsync<T>(source, address, progress, token), onCompleted, onError, onCancelled);

        public IEnumerator RentAsync<T>(ResourceSource source, string address,
            Action<InstanceLease<T>> onCompleted = null,
            Action<Exception> onError = null,
            Action onCancelled = null,
            CancellationToken token = default)
            => ObserveCoroutine(RentAsync<T>(source, address, token), onCompleted, onError, onCancelled);

        public IEnumerator UnloadAsync(Func<ResourceKey, bool> selector = null,
            Action<UnloadResult> onCompleted = null,
            Action<Exception> onError = null)
            => UnloadAsync(selector).ToCoroutine(onCompleted, onError);

        #endregion

        internal static IEnumerator ObserveCoroutine<T>(
            Task<T> task,
            Action<T> onCompleted,
            Action<Exception> onError,
            Action onCancelled)
        {
            return task.ToCoroutine(onCompleted, ex =>
            {
                if (ex is OperationCanceledException) onCancelled?.Invoke();
                else onError?.Invoke(ex);
            });
        }

        private static InvalidOperationException NotInitialized()
            => new InvalidOperationException("资源系统尚未初始化成功，请先 await InitializeAsync()。");
    }
}
