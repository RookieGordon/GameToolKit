/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : 资源引用凭证的来源类型。
 */

namespace ToolKit.Tools.Common
{
    public enum ERefKind
    {
        /// <summary> 资源型: 背后是共享的 AssetHandle (Sprite/Material/byte[]/直接使用的预制体等) </summary>
        Asset = 0,

        /// <summary> 实例型: 背后是从对象池取出的实例 (当前为 GameObject) </summary>
        Instance = 1,
    }
}
