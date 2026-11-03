using System;
using System.Resources;
using System.Threading;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.UI;
using ResourceManager = UnityToolKit.Runtime.ResourceSystem.ResourceManager;

namespace UnityToolKit.Engine.UI
{
    public class RawImageExt: RawImage
    {
        private ResourceManager _resourceManager;
        
        private ResourceRef<Texture2D> _texRef;

        protected override void Awake()
        {
            _resourceManager = ResourceManager.Instance;
            base.Awake();
        }
        
        public void SetTexture(string address,
            Action<Exception> onError = null,
            Action onCancelled = null,
            CancellationToken token = default)
        {
            ClearTexture();
            void OnSuccess(ResourceRef<Texture2D> resourceRef)
            {
                if (this == null)
                {
                    resourceRef.Dispose();
                    return;
                }

                this.texture = resourceRef.Value;
                _texRef = resourceRef;
            }

            var ie = _resourceManager.LoadAsync<Texture2D>(ResourceSource.AssetBundle, address, null,
                OnSuccess, onError, onCancelled, token);
            _resourceManager.StartCoroutine(ie);
        }

        private void ClearTexture()
        {
            if (this.texture != null && _texRef != null)
            {
                this.texture = null;
                _texRef.Dispose();
            }
        }

        protected override void OnDestroy()
        {
            ClearTexture();
            _texRef = null;
            base.OnDestroy();
        }
    }
}