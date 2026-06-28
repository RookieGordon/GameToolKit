/*
 * author       : Gordon
 * datetime     : 2026/6/27
 * description  : 资源加载错误码。预定义、可读、可分类, 取代裸 Exception。
 *                既用于加载结果错误 (挂在句柄/凭证的 LoadError 上),
 *                也用于配置/误用错误 (抛出的 ResourceException.Code)。
 */

namespace ToolKit.Tools.Common
{
    public enum ELoadError
    {
        /// <summary> 无错误 </summary>
        None = 0,

        /// <summary> 未知错误 </summary>
        Unknown,

        /// <summary> 地址为空或格式非法 </summary>
        InvalidAddress,

        /// <summary> 找不到可处理该地址的加载器 (配置/误用) </summary>
        NoLoader,

        /// <summary> 资源 / 文件不存在 </summary>
        NotFound,

        /// <summary> 远端下载失败 (网络) </summary>
        NetworkError,

        /// <summary> 读盘 / IO 失败 </summary>
        IOError,

        /// <summary> 该资源不可被实例化/池化 (角色护栏, 误用) </summary>
        NotInstantiable,

        /// <summary> 未注册实例器, 不支持实例化 (误用) </summary>
        InstancerMissing,

        /// <summary> 资源管理器已释放 (误用) </summary>
        Disposed,

        /// <summary> 已取消 </summary>
        Cancelled,
    }
}
