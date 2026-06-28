/*
 * author       : Gordon
 * datetime     : 2026/6/27
 * description  : IBackingSource 的获取结果 (引擎无关, 程序集内部)。
 *                成功携带背书 (IRefBacking), 失败携带结构化 LoadError —— 以便门面把错误透传到失败凭证上。
 */

namespace ToolKit.Tools.Common
{
    internal readonly struct AcquireResult
    {
        public readonly IRefBacking Backing;
        public readonly LoadError Error;

        public AcquireResult(IRefBacking backing)
        {
            Backing = backing;
            Error = LoadError.None;
        }

        public AcquireResult(LoadError error)
        {
            Backing = null;
            Error = error;
        }

        public bool Ok => Backing != null;
    }
}
