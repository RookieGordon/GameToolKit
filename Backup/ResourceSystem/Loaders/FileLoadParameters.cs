/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 文件加载器的不可变参数载荷 (R18)。RequestOptions.Parameters 传入本类型：
 *                ConversionKey 是影响转换结果的稳定参数键，参与内存资源身份。
 *                转换器由目标类型唯一确定；不支持的参数类型在解析阶段明确报错。
 */

using System.Collections.Generic;

namespace ToolKit.Tools.Common
{
    public sealed class FileLoadParameters
    {
        public readonly string? ConversionKey;

        public FileLoadParameters(string? conversionKey = null)
        {
            // 空字符串与未传参数使用同一身份，也给转换器传入同一个值。
            ConversionKey = string.IsNullOrEmpty(conversionKey) ? null : conversionKey;
        }

        internal static string? GetConversionKey(object? parameters)
        {
            switch (parameters)
            {
                case null:
                    return null;
                case FileLoadParameters typed:
                    return typed.ConversionKey;
                default:
                    // 未支持的参数必须明确报错，不静默忽略 (§3.3)
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                        new Dictionary<string, object>
                        {
                            { "parametersType", parameters.GetType().Name },
                            { "reason", "Parameters 须为 FileLoadParameters 或 null" },
                        }));
            }
        }
    }
}
