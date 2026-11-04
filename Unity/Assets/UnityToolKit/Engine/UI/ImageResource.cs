using System;
using System.Threading;
using ToolKit.Tools.Common;
using UnityToolKit.Runtime.ResourceSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace UnityToolKit.Engine.UI
{
    /// <summary>每个图片组件独占一份加载状态与资源引用；所有入口均在 Unity 主线程调用。</summary>
    internal sealed class ImageResource<T> : IDisposable where T : Object
    {
        private readonly Graphic _owner;
        private readonly Action<T> _setValue;
        private ResourceRef<T> _current;
        private CancellationTokenSource _request;
        private ResourceSource _source;
        private string _address;
        private bool _disposed;

        public ImageResource(Graphic owner, Action<T> setValue)
        {
            _owner = owner;
            _setValue = setValue;
        }

        public async void Set(string address, ResourceSource source, bool nativeSize,
            Action<Exception> onError, Action onCancelled)
        {
            if (_disposed || _owner == null) return;
            if (_request != null && _source == source
                && string.Equals(_address, address, StringComparison.Ordinal)) return;

            var previousRequest = _request;
            var request = new CancellationTokenSource();
            // 先登记新请求；取消旧请求的回调可以重入并取代本轮。
            _request = request;
            _source = source;
            _address = address;
            ResourceRef<T> pending = null;
            Exception failure = null;
            var cancelled = false;
            try
            {
                previousRequest?.Cancel();
                request.Token.ThrowIfCancellationRequested();
                if (!_owner.didAwake)
                    throw new InvalidOperationException("请在图片组件 Awake 后加载资源。");
                // 保留调用者的主线程上下文，交付图片时仍可安全访问 Unity 对象。
                pending = await ResourceManager.Instance.LoadAsync<T>(source, address, null, request.Token);
                cancelled = request.IsCancellationRequested || _disposed || _owner == null;
                if (!cancelled)
                {
                    var next = pending;
                    pending = null;
                    _request = null;
                    Replace(next, nativeSize);
                }
            }
            catch (Exception) when (request.IsCancellationRequested) { cancelled = true; }
            catch (Exception error) { failure = error; }
            finally
            {
                // 取消不等于后端已结束，迟到但未交付的引用也必须归还。
                pending?.Dispose();
                if (ReferenceEquals(_request, request)) _request = null;
                request.Dispose();
            }
            // 本轮状态清理后才通知业务，回调可安全发起下一次加载。
            if (cancelled) onCancelled?.Invoke();
            else if (failure != null) onError?.Invoke(failure);
        }

        public void Cancel()
        {
            var request = _request;
            _request = null;
            request?.Cancel();
        }

        public void Clear() => Replace(null, false);

        private void Replace(ResourceRef<T> next, bool nativeSize)
        {
            var previous = _current;
            _current = next;
            try
            {
                // setter 会同步触发 UI 回调，先交接引用，再调用外部代码。
                _setValue(next == null ? null : next.Value);
                if (nativeSize && !_disposed && _owner != null && ReferenceEquals(_current, next))
                    _owner.SetNativeSize();
            }
            finally { previous?.Dispose(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Cancel(); }
            finally { Clear(); }
        }
    }
}
