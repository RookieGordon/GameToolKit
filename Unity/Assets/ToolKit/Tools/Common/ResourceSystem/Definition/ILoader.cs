/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : 加载器接口。框架的核心扩展点 —— 任何加载来源 (本地/远程/Resources/AssetBundle/
 *                第三方) 都通过实现 ILoader 接入。ILoader 只负责"把某个地址变成一个底层资源句柄",
 *                不关心引用计数、缓存、加载锁, 这些统一由 ResourceManager 负责。
 */

using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public interface ILoader
    {
        /// <summary> 该加载器负责处理的加载来源类型 </summary>
        ELoadType LoadType { get; }

        /// <summary>
        /// 加载器允许同时执行的最大加载数。小于等于 0 表示不限制。
        /// </summary>
        int MaxConcurrentLoads { get; }

        /// <summary>
        /// 是否能处理给定地址。ResourceManager 在 ELoadType.Auto 时, 依次询问已注册的加载器。
        /// </summary>
        bool CanLoad(string address);

        /// <summary>
        /// 异步加载资源。实现方应当: 创建并返回一个 AssetHandle, 设置其底层资源对象与卸载委托。
        /// <para>加载不上报通用进度 (大多数加载瞬时完成); 远端下载进度请用 RemoteFileLoader.OnDownloadProgress。</para>
        /// </summary>
        /// <param name="address">资源地址</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task<IAssetHandle> LoadAsync(string address, CancellationToken cancellationToken = default);
    }
}
