/*
 * author       : Gordon
 * datetime     : 2026/6/27
 * description  : 资源系统的 typed 异常, 携带 ELoadError 码 + 人类可读信息。
 *                用于配置/误用类错误 (fail-fast): 找不到加载器、不可池化、未注册实例器、已释放等。
 *                运行时的加载结果错误不抛此异常, 而是挂在句柄/凭证的 LoadError 上。
 */

using System;

namespace ToolKit.Tools.Common
{
    public sealed class ResourceException : Exception
    {
        public ELoadError Code { get; }

        public ResourceException(ELoadError code, string message, Exception inner = null)
            : base(message, inner)
        {
            Code = code;
        }

        public override string ToString()
        {
            return $"[ResourceException:{Code}] {Message}";
        }
    }
}
