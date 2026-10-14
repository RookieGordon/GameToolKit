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
        /// 失败时回收中间对象；释放失败时通过 ResourceLoadException.CleanupRemainders
        /// 交出实际残留对象。仅有 Unknown 错误不会制造空的脏记录或封锁资源地址。
        /// </summary>
        Task<LoadedAsset> ConvertAsync(string path, Type resultType, string? conversionKey, CancellationToken ct);
    }
}

