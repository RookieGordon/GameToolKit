using System;
using System.Collections.Generic;

namespace ToolKit.Tools.Common
{
    /// <summary> 文件目标类型到转换实现的直接映射；不同类型可以注册同一个转换器实例。 </summary>
    public sealed class FileResourceConverters
    {
        private readonly object _gate = new object();
        private readonly Dictionary<Type, IFileResourceConverter> _converters =
            new Dictionary<Type, IFileResourceConverter>();

        public static FileResourceConverters CreateDefault()
        {
            var converters = new FileResourceConverters();
            converters.Register(typeof(byte[]), new BytesFileConverter());
            converters.Register(typeof(string), new TextFileConverter());
            return converters;
        }

        public void Register(Type resultType, IFileResourceConverter converter)
        {
            if (resultType == null) throw new ArgumentNullException(nameof(resultType));
            if (converter == null) throw new ArgumentNullException(nameof(converter));
            lock (_gate)
            {
                if (_converters.ContainsKey(resultType))
                {
                    throw new InvalidOperationException($"目标类型 {resultType.FullName} 已注册文件资源转换器。");
                }
                _converters.Add(resultType, converter);
            }
        }

        public IFileResourceConverter Get(Type resultType)
        {
            if (resultType == null) throw new ArgumentNullException(nameof(resultType));
            lock (_gate)
            {
                if (_converters.TryGetValue(resultType, out var converter))
                {
                    return converter;
                }
            }
            throw new ResourceLoadException(new LoadError(
                DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, CleanupStatus.Complete, null,
                new Dictionary<string, object> { { "resultType", resultType.FullName ?? resultType.Name } }));
        }
    }
}
