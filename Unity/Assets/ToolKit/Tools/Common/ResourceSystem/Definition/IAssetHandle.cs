/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : 资源句柄接口。一个句柄对应一份"只加载一次"的底层资源 (文件/Asset)。
 *                句柄持有引用计数, 计数归零后由 ResourceManager 触发真正的卸载。
 */

namespace ToolKit.Tools.Common
{
    public interface IAssetHandle
    {
        /// <summary> 资源地址 (加载时的 key, 全局唯一) </summary>
        string Address { get; }

        /// <summary> 当前加载状态 </summary>
        ELoadStatus Status { get; }

        /// <summary> 当前引用计数 </summary>
        int ReferenceCount { get; }

        /// <summary> 是否加载成功 </summary>
        bool IsSuccess { get; }

        /// <summary> 加载失败/取消时的结构化错误 (码 + 可读信息); 成功时 Code 为 None </summary>
        LoadError Error { get; }

        /// <summary>
        /// 增加一次引用计数。每一次 Retain 都应当对应一次 Release。
        /// </summary>
        void Retain();

        /// <summary>
        /// 释放一次引用计数。计数归零时, 句柄对应的底层资源会被卸载。
        /// </summary>
        void Release();

        /// <summary>
        /// 获取底层资源对象。
        /// <para>本地/远程文件返回 byte[]; Resources/AssetBundle 返回对应的 UnityEngine.Object。</para>
        /// </summary>
        T GetAsset<T>() where T : class;
    }
}
