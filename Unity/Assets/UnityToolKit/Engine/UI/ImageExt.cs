using System;
using System.Resources;
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

        protected override void Awake()
        {
            _resourceManager = ResourceManager.Instance;
            base.Awake();
        }
        
        public void SetSprite(string address,
            Action<Exception> onError = null,
            Action onCancelled = null,
            CancellationToken token = default)
        {
            ClearSprite();
            void OnSuccess(ResourceRef<Sprite> resourceRef)
            {
                if (this == null)
                {
                    resourceRef.Dispose();
                    return;
                }

                this.sprite = resourceRef.Value;
                _spriteRef = resourceRef;
            }

            var ie = _resourceManager.LoadAsync<Sprite>(ResourceSource.AssetBundle, address, null,
                OnSuccess, onError, onCancelled, token);
            _resourceManager.StartCoroutine(ie);
        }

        private void ClearSprite()
        {
            if (this.sprite != null && _spriteRef != null)
            {
                this.sprite = null;
                _spriteRef.Dispose();
            }
        }

        protected override void OnDestroy()
        {
            ClearSprite();
            _spriteRef = null;
            base.OnDestroy();
        }
    }
}