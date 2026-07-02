using System;
using System.Collections.Generic;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace UnityToolKit.Runtime.Resource
{
    public static class UResourceManagerExtension
    {
        private static IApplicable _imageSpriteApplicator = new ImageSpriteApplicator();
        
        private static IApplicable _rawImageTextureApplicator = new RawImageTextureApplicator();

        #region 加载AssetBundle资源

        public static void SetSprite(this Image self, string address, bool setNativeSize, Action onComplete)
        {
            UResourceManager.Instance.ApplyAssetAsync<Image, Sprite>(self, address, _imageSpriteApplicator, onComplete, default, setNativeSize);
        }
        
        public static void RestSetSprite(this Image self)
        {
            UResourceManager.Instance.RevertAsset<Image>(self, _imageSpriteApplicator);
        }

        public static void SetTexture(this RawImage self, string address, bool setNativeSize, Action onComplete)
        {
            UResourceManager.Instance.ApplyAssetAsync<RawImage, Texture>(self, address, _rawImageTextureApplicator, onComplete, default, setNativeSize);
        }
        
        public static void RestSetTexture(this RawImage self)
        {
            UResourceManager.Instance.RevertAsset<RawImage>(self, _rawImageTextureApplicator);
        }

        public static void SetMaterial(this Renderer self, string address, Action onComplete = null, Action onFailure = null)
        {
            
        }

        public static void SetAnimation(this Animation self, string address, Action onComplete = null, Action onFailure = null)
        {
            
        }

        public static void SetAudioClip(this AudioSource self, string address, Action onComplete = null, Action onFailure = null)
        {
            
        }

        public static void SetVideoClip(this VideoPlayer self, string address, Action onComplete = null, Action onFailure = null)
        {
            
        }

        public static void SetAnimationController(this Animator self, string address, Action onComplete = null, Action onFailure = null)
        {
           
        }

        #endregion

        #region 加载Resourcr资源

        public static void SetResourceSprite(this Image self, string address, bool setNativeSize, Action onComplete)
        {
            UResourceManager.Instance.ApplyResourceAsync<Image, Sprite>(self, address, _imageSpriteApplicator, onComplete, default, setNativeSize);
        }

        public static void SeResourceTexture(this RawImage self, string address, bool setNativeSize, Action onComplete)
        {
            UResourceManager.Instance.ApplyResourceAsync<RawImage, Texture>(self, address, _rawImageTextureApplicator, onComplete, default, setNativeSize);
        }

        #endregion
        
    }
}