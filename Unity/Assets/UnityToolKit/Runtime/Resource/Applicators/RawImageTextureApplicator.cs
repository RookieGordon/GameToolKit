using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.UI;

namespace UnityToolKit.Runtime.Resource
{
    public class RawImageTextureApplicator: IApplicable
    {
        public void Apply<T, R>(T target, R resource, params object[] applayArgs) where T : class where R : class
        {
            if (target == null || resource == null)
            {
                return;
            }

            if ((target is RawImage image) && (resource is Texture texture))
            {
                image.texture = texture;
                var needSetNativeSize = (applayArgs != null && applayArgs.Length >= 1) ? (bool)applayArgs[0] : false;
                if (needSetNativeSize)
                {
                    image.SetNativeSize();
                }
            }
        }

        public void Revert<T>(T target) where T : class
        {
            if (target is RawImage image)
            {
                image.texture = null;
            }
        }
    }
}