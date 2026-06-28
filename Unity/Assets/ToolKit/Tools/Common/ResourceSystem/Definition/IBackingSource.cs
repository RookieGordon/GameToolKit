/*
 * author       : Gordon
 * datetime     : 2026/6/27
 * description  : 凭证背书来源 (引擎无关, 程序集内部)。
 *                统一抽象"按 key 产出一份可释放持有(IRefBacking)"这件事:
 *                  - SharedAssetSource   : 产出共享资源句柄背书 (AssetBacking);
 *                  - PooledInstanceSource: 产出池化实例背书 (InstanceBacking)。
 *                门面据此把 LoadRefAsync / InstantiateRefAsync 收束为同一条发放路径,
 *                也为"按资源类型组合来源"留出扩展点。
 */

using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    internal interface IBackingSource
    {
        /// <summary>
        /// 按 key 获取一份可释放持有。成功携带背书, 失败携带结构化 LoadError。
        /// </summary>
        Task<AcquireResult> AcquireAsync(
            string key,
            ELoadType loadType = ELoadType.Auto,
            CancellationToken cancellationToken = default);
    }
}
