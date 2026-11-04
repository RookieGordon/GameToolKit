using System;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.UI;

namespace UnityToolKit.Engine.UI
{
    public class ImageExt: Image
    {
        private ImageResource<Sprite> _imgResource;
        private ImageResource<Sprite> ImgResource =>
            _imgResource ??= new ImageResource<Sprite>(this, value => sprite = value);

        [InspectorName("是否空白Image")]
        public bool IsEmpty = false;

        /// <summary>
        /// 替换成功后自动归还旧引用；失败或取消保留当前图片。
        /// 同来源、同地址正在加载时跳过；重复调用的参数与回调不生效。请在 Awake 后的主线程调用。
        /// </summary>
        public void SetSprite(string address,
            ResourceSource resSource = ResourceSource.AssetBundle,
            bool isNativeSize = false,
            Action<Exception> onError = null,
            Action onCancelled = null)
            => ImgResource.Set(address, resSource, isNativeSize, onError, onCancelled);

        /// <summary>取消在途替换，保留当前图片；结果仍会被观察并清理。</summary>
        public void CancelSet() => _imgResource?.Cancel();

        /// <summary>清空当前显示并归还引用；需要取消在途替换时先调用 CancelSet。</summary>
        public void ClearSprite() => ImgResource.Clear();

        protected override void OnDestroy()
        {
            try { _imgResource?.Dispose(); }
            finally { base.OnDestroy(); }
        }
    }
}
