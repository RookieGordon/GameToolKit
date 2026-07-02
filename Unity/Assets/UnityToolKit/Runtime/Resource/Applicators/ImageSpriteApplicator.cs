using System.Collections.Generic;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.UI;

namespace UnityToolKit.Runtime.Resource
{
    public class ImageSpriteApplicator: IApplicable
    {
        public void Apply<T, R>(T target, R resource, params object[] applayArgs) where T : class where R : class
        {
            if (target == null || resource == null)
            {
                return;
            }

            if ((target is Image image) && (resource is Sprite sprite))
            {
                image.sprite = sprite;
                var needSetNativeSize = (applayArgs != null && applayArgs.Length >= 1) ? (bool)applayArgs[0] : false;
                if (needSetNativeSize)
                {
                    image.SetNativeSize();
                }
            }
        }

        public void Revert<T>(T target) where T : class
        {
            if (target is Image image)
            {
                image.sprite = null;
            }
        }
    }
}