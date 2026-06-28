/*
 * author       : Gordon
 * datetime     : 2026/6/27
 * description  : 资源型背书。背后是共享的 AssetHandle (Sprite/Material/byte[]/直接使用的预制体)。
 */

namespace ToolKit.Tools.Common
{
    internal sealed class AssetBacking : IRefBacking
    {
        private readonly IAssetHandle _handle;

        public AssetBacking(IAssetHandle handle)
        {
            _handle = handle;
        }

        public ERefKind Kind => ERefKind.Asset;
        public string Address => _handle?.Address;

        public T Get<T>() where T : class => _handle?.GetAsset<T>();

        public bool IsAlive => _handle != null && _handle.IsSuccess;

        public void Release() => _handle?.Release();

        public IRefBacking AcquireClone()
        {
            if (_handle == null || !_handle.IsSuccess)
            {
                return null;
            }
            _handle.Retain(); // 独立引用
            return new AssetBacking(_handle);
        }
    }
}
