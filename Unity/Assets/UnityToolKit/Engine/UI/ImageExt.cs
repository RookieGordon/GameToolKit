using System;
using System.Threading;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.UI;
using ResourceManager = UnityToolKit.Runtime.ResourceSystem.ResourceManager;

namespace UnityToolKit.Engine.UI
{
    public class ImageExt: Image
    {
        private ResourceManager _resourceManager;

        private ResourceRef<Sprite> _spriteRef;

        [InspectorName("是否空白Image")]
        public bool IsEmpty = false;

        private CancellationTokenSource _loadCancellation;
        private ResourceSource _loadSource;
        private string _loadAddress;
        private bool _destroyed;

        protected override void Awake()
        {
            _resourceManager = ResourceManager.Instance;
            base.Awake();
        }

        /// <summary>
        /// 替换成功后自动归还旧引用；失败或取消保留当前图片。
        /// 同来源、同地址正在加载或已持有并显示时跳过；重复调用的参数与回调不生效。
        /// 重新选择当前图片会取消其他在途替换。
        /// </summary>
        public async void SetSprite(string address,
            ResourceSource resSource = ResourceSource.AssetBundle,
            bool isNativeSize = false,
            Action<Exception> onError = null,
            Action onCancelled = null)
        {
            if (_destroyed || this == null) return;
            if (_loadCancellation != null && _loadSource == resSource
                && string.Equals(_loadAddress, address, StringComparison.Ordinal)) return;
            if (_spriteRef != null && _spriteRef.Key.Source == resSource
                && string.Equals(_spriteRef.Key.Address, address, StringComparison.Ordinal)
                && sprite != null && sprite == _spriteRef.Value)
            {
                CancelSet();
                return;
            }
            var previousRequest = _loadCancellation;
            var request = new CancellationTokenSource();
            // 先安装新请求，旧请求取消通知即使重入，也不能被本轮覆盖。
            _loadCancellation = request;
            _loadSource = resSource;
            _loadAddress = address;
            ResourceRef<Sprite> pending = null;
            Exception failure = null;
            var cancelled = false;
            try
            {
                // 先取消上一次可能存在的加载
                previousRequest?.Cancel();
                request.Token.ThrowIfCancellationRequested();
                pending = await _resourceManager.LoadAsync<Sprite>(resSource, address, null, request.Token);
                cancelled = request.IsCancellationRequested || _destroyed || this == null
                    || !ReferenceEquals(_loadCancellation, request);
                if (!cancelled)
                {
                    var value = pending.Value;
                    var previous = _spriteRef;
                    _spriteRef = pending;
                    pending = null;
                    _loadCancellation = null;
                    // UI setter 会同步触发 dirty 回调；先交接所有权，再调用外部代码。
                    try
                    {
                        sprite = value;
                        if (isNativeSize)
                        {
                            SetNativeSize();
                        }
                    }
                    finally { previous?.Dispose(); }
                }
            }
            catch (Exception) when (request.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (Exception error) { failure = error; }
            finally
            {
                // 即使取消时 Task 已成功，未交付的引用仍由本轮归还。
                pending?.Dispose();
                if (ReferenceEquals(_loadCancellation, request)) _loadCancellation = null;
                request.Dispose();
            }
            if (cancelled) onCancelled?.Invoke();
            else if (failure != null) onError?.Invoke(failure);
        }

        /// <summary>取消在途替换，保留当前图片；结果仍会被观察并清理。</summary>
        public void CancelSet()
        {
            var request = _loadCancellation;
            _loadCancellation = null;
            request?.Cancel();
        }

        /// <summary>清空当前显示并归还引用；需要取消在途替换时先调用 CancelSet。</summary>
        public void ClearSprite()
        {
            var previous = _spriteRef;
            _spriteRef = null;
            try { sprite = null; }
            finally { previous?.Dispose(); }
        }

        protected override void OnDestroy()
        {
            _destroyed = true;
            try
            {
                CancelSet();
                ClearSprite();
            }
            finally { base.OnDestroy(); }
        }
    }
}
