/*
 * author       : Gordon
 * datetime     : 2026/6/27
 * description  : 结构化加载错误: 错误码 + 人类可读信息 + 原始异常(可空, 留作诊断)。
 *                挂在 IAssetHandle.Error / ResourceRef.Error 上, 取代裸 Exception。
 */

using System;

namespace ToolKit.Tools.Common
{
    public readonly struct LoadError
    {
        /// <summary> 预定义错误码 </summary>
        public readonly ELoadError Code;

        /// <summary> 人类可读信息 </summary>
        public readonly string Message;

        /// <summary> 原始异常 (可空), 保留以便查堆栈 </summary>
        public readonly Exception Inner;

        public LoadError(ELoadError code, string message, Exception inner = null)
        {
            Code = code;
            Message = message;
            Inner = inner;
        }

        public bool IsError => Code != ELoadError.None;

        public static readonly LoadError None = default;

        public override string ToString()
        {
            return IsError ? $"[{Code}] {Message}" : "[None]";
        }
    }
}
