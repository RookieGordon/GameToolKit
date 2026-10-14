namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 业务已知的资源来源。装配时绑定到实际加载器；不规定目录、传输方式或加载器实现。
    /// 每次请求必须显式指定来源。同一来源可以在配置期替换加载器实现。
    /// </summary>
    public enum ResourceSource
    {
        /// <summary> Unity Resources 资源。 </summary>
        Resources = 1,

        /// <summary> AssetBundle 资源；可接入简单 AB、Addressables 或 YooAsset 加载器。 </summary>
        AssetBundle = 2,

        /// <summary> 本地文件资源。 </summary>
        Local = 3,

        /// <summary> 远端资源。具体协议与本地复用由加载器处理。 </summary>
        Remote = 4,
    }
}
