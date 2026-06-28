/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : 资源加载来源类型。用于 ResourceManager 将地址路由到对应的 ILoader。
 *                抽象层只定义枚举值, 不引用任何引擎类型, 因此可放在 noEngineReferences 程序集中。
 */

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 资源加载来源类型
    /// </summary>
    public enum ELoadType
    {
        /// <summary> 未指定, 交由 ResourceManager 按地址协议自动推断 </summary>
        Auto = 0,

        /// <summary> 本地文件 (持久化目录 / 绝对路径), 引擎无关 </summary>
        LocalFile = 1,

        /// <summary> 远程文件 (http/https), 下载后落地到本地缓存, 引擎无关 </summary>
        RemoteFile = 2,

        /// <summary> Unity Resources 目录加载 (引擎相关, 由 Engine 程序集实现) </summary>
        Resources = 10,

        /// <summary> Unity AssetBundle 加载 (引擎相关, 由 Engine 程序集实现) </summary>
        AssetBundle = 11,

        /// <summary> 第三方加载器 (如 Addressables / YooAsset), 由业务自行注册 </summary>
        Custom = 100,
    }
}
