/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 文件资源转换契约。本地和远端文件加载器共用，按目标类型选择实现。
 */

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary> 将本地文件转换为目标资源；成功结果自带释放操作，源文件仍由文件所有者管理。 </summary>
    public interface IFileResourceConverter
    {
        /// <summary>
        /// conversionKey 是请求提供的转换差异标识，由具体实现解释，并参与资源缓存身份。
        /// 失败时清理创建的中间对象；无法确认清理完成时，在 ResourceLoadException 中保留清理状态。
        /// </summary>
        Task<LoadedAsset> ConvertAsync(string path, Type resultType, string? conversionKey, CancellationToken ct);
    }
}

