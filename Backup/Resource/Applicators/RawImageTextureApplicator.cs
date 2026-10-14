/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : RawImage.texture 应用器 (P6, §12.1)。异常安全替换与先解绑再释放的 Revert。
 */

using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.UI;

namespace UnityToolKit.Runtime.Resource
{
    public sealed class RawImageTextureApplicator : IResourceApplicator<RawImage, Texture>
    {
        public static readonly RawImageTextureApplicator Instance = new RawImageTextureApplicator();

        public void Replace(RawImage target, Texture value)
        {
            var old = target.texture;
            target.texture = value;
            if (target.texture != value)
            {
                target.texture = old;
                throw new System.InvalidOperationException("RawImage.texture 赋值未生效");
            }
        }

        public void Revert(RawImage target)
        {
            target.texture = null;
        }
    }
}
