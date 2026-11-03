using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 按两个键存取值的嵌套字典。Count、Keys、Values 和枚举仍针对外层分组。
    /// </summary>
    /// <remarks>
    /// 双键写入自动创建分组，双键删除在移除最后一项时清理分组。
    /// 使用继承的单键接口时，内层字典不得为 null；直接修改内层字典不会自动清理分组。
    /// 与 Dictionary 一样，不保证线程安全。
    /// </remarks>
    public class MultiDictionary<TKey1, TKey2, TValue> : Dictionary<TKey1, Dictionary<TKey2, TValue>>
        where TKey1 : notnull
        where TKey2 : notnull
    {
        /// <summary>读取指定值；任一键不存在时抛出异常。赋值会新增或覆盖指定值。</summary>
        public TValue this[TKey1 key1, TKey2 key2]
        {
            get
            {
                ValidateKeys(key1, key2);
                return base[key1][key2];
            }
            set
            {
                ValidateKeys(key1, key2);
                if (base.TryGetValue(key1, out var dictionary))
                {
                    dictionary[key2] = value;
                }
                else
                {
                    Add(key1, key2, value);
                }
            }
        }

        /// <summary>添加值；两个键的组合已存在时抛出 ArgumentException。</summary>
        public void Add(TKey1 key1, TKey2 key2, TValue value)
        {
            ValidateKeys(key1, key2);
            if (base.TryGetValue(key1, out var dictionary))
            {
                dictionary.Add(key2, value);
                return;
            }

            // 内层添加成功后再发布分组，避免失败时留下空分组。
            dictionary = new Dictionary<TKey2, TValue> { { key2, value } };
            base.Add(key1, dictionary);
        }

        /// <summary>判断指定的双键组合是否存在。</summary>
        public bool ContainsKey(TKey1 key1, TKey2 key2)
        {
            return TryGetValue(key1, key2, out _);
        }

        /// <summary>尝试读取值；任一键不存在时返回 false，value 为默认值。</summary>
        public bool TryGetValue(TKey1 key1, TKey2 key2, [MaybeNullWhen(false)] out TValue value)
        {
            ValidateKeys(key1, key2);
            value = default;
            return base.TryGetValue(key1, out var dictionary) && dictionary.TryGetValue(key2, out value);
        }

        /// <summary>仅在实际移除值时返回 true；移除最后一项后同时移除外层分组。</summary>
        public bool Remove(TKey1 key1, TKey2 key2)
        {
            ValidateKeys(key1, key2);
            if (!base.TryGetValue(key1, out var dictionary) || !dictionary.Remove(key2))
            {
                return false;
            }

            if (dictionary.Count == 0)
            {
                base.Remove(key1);
            }

            return true;
        }

        private static void ValidateKeys(TKey1 key1, TKey2 key2)
        {
            if (key1 == null) throw new ArgumentNullException(nameof(key1));
            if (key2 == null) throw new ArgumentNullException(nameof(key2));
        }
    }
}
