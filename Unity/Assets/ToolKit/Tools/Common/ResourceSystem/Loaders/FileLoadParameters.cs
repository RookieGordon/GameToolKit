/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 文件加载器的不可变参数载荷 (R18)。RequestOptions.Parameters 传入本类型：
 *                DecoderId 显式选择解码器；DecodeKey 是影响解码结果的稳定参数键 (进 LocalKey)。
 *                兼容直接传 string 作为 DecoderId。不支持的参数类型在解析阶段明确报错。
 */

using System.Collections.Generic;
using ToolKit.Tools.Common;

namespace ToolKit.Tools.Common
{
    public sealed class FileLoadParameters
    {
        public readonly string? DecoderId;
        public readonly string? DecodeKey;

        public FileLoadParameters(string? decoderId = null, string? decodeKey = null)
        {
            DecoderId = decoderId;
            DecodeKey = decodeKey;
        }

        internal static FileLoadParameters From(object? parameters)
        {
            switch (parameters)
            {
                case null:
                    return new FileLoadParameters();
                case FileLoadParameters typed:
                    return typed;
                case string decoderId:
                    return new FileLoadParameters(decoderId);
                default:
                    // 未支持的参数必须明确报错，不静默忽略 (§3.3)
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                        new Dictionary<string, object>
                        {
                            { "parametersType", parameters.GetType().Name },
                            { "reason", "Parameters 须为 FileLoadParameters、string(解码器 Id) 或 null" },
                        }));
            }
        }
    }
}
