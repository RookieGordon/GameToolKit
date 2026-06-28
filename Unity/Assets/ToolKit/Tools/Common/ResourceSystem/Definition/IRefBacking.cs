/*
 * author       : Gordon
 * datetime     : 2026/6/27
 * description  : 凭证背书 (引擎无关, 程序集内部)。把"怎么取对象 / 是否有效 / 怎么释放 / 能否复制"
 *                这套随来源不同的行为抽象出来, 由发放凭证的层注入。ResourceRef 只持有一个 IRefBacking,
 *                从而把原先 ResourceRef/ReleaseRef 里的 if(kind==Asset/Instance) 分支化为多态。
 */

namespace ToolKit.Tools.Common
{
    internal interface IRefBacking
    {
        ERefKind Kind { get; }
        string Address { get; }

        /// <summary> 取底层对象 </summary>
        T Get<T>() where T : class;

        /// <summary> 底层是否仍有效 (资源加载成功 / 实例未被引擎销毁) </summary>
        bool IsAlive { get; }

        /// <summary> 释放本背书所代表的一次持有 (资源型: 句柄 -1; 实例型: 实例归还池) </summary>
        void Release();

        /// <summary>
        /// 复制出一份独立背书 (对应 copyRef)。资源型: 句柄 Retain 后返回新背书; 实例型: 返回 null (不支持)。
        /// </summary>
        IRefBacking AcquireClone();
    }
}
