using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;

namespace UnityToolKit.Runtime.ResourceSystem
{
    /// <summary>
    /// 内置转换：byte[] (PNG/JPG) → Texture2D；产物释放时 Destroy 纹理。注册到目标类型 Texture2D
    /// </summary>
    public sealed class TextureConverter : IProductConverter
    {
        private readonly bool _readable;

        /// <param name="readable">是否保留 CPU 侧可读副本；false 更省内存</param>
        public TextureConverter(bool readable = false)
        {
            _readable = readable;
        }

        public Task<IResource> ConvertAsync(IResource source, ResourceKey key, CancellationToken cancellation)
        {
            if (!(source.Value is byte[] bytes))
                throw ResourceException.Of(DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, key);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, bytes, !_readable))
            {
                Object.Destroy(texture);
                throw ResourceException.Of(DiagnosticCodes.AssetConversionFailed, LoadStage.Convert, key);
            }
            return Task.FromResult<IResource>(new Product(texture, () => { if (texture != null) Object.Destroy(texture); }));
        }
    }
}