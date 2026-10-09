/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : Image.sprite 应用器 (P6, §12.1)。异常安全：Replace 失败恢复旧值后再抛；
 *                Revert 先置 null 解除引用 (由绑定器随后释放底层持有)。
 */

using ToolKit.Tools.Common;
using UnityEngine;
using UnityEngine.UI;

namespace UnityToolKit.Runtime.Resource
{
    public sealed class ImageSpriteApplicator : IResourceApplicator<Image, Sprite>
    {
        public static readonly ImageSpriteApplicator Instance = new ImageSpriteApplicator();

        public void Replace(Image target, Sprite value)
        {
            var old = target.sprite;
            target.sprite = value;
            if (target.sprite != value) // 属性拒绝赋值等异常场景：恢复旧值
            {
                target.sprite = old;
                throw new System.InvalidOperationException("Image.sprite 赋值未生效");
            }
        }

        public void Revert(Image target)
        {
            target.sprite = null; // 先解除 UI 对资源的引用，绑定器随后释放底层持有
        }
    }
}
