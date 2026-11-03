using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using ToolKit.Tools.Extension;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 按两个键存取列表的嵌套字典。Count、Keys、Values 和枚举仍针对外层分组。
    /// </summary>
    /// <remarks>
    /// 内层使用 ListDictionary；添加值时自动创建分组和列表，默认允许重复值。
    /// 删除值时只移除第一次匹配，删除操作会逐层清理被删空的分组。
    /// 内层字典和列表不得为 null；通过继承接口或直接修改列表时，不会自动清理外层分组。
    /// 读取返回原列表，索引赋值和整组删除不会清空外部持有的旧列表。与 Dictionary 一样，不保证线程安全。
    /// </remarks>
    public class MultiListDictionary<TKey1, TKey2, TValue> : Dictionary<TKey1, ListDictionary<TKey2, TValue>>
        where TKey1 : notnull
        where TKey2 : notnull
    {
        /// <summary>读取指定列表；任一键不存在时抛出异常。赋值会新增或替换列表。</summary>
        public List<TValue> this[TKey1 key1, TKey2 key2]
        {
            get
            {
                ValidateKeys(key1, key2);
                return base[key1][key2];
            }
            set
            {
                ValidateKeys(key1, key2);
                if (value == null) throw new ArgumentNullException(nameof(value));

                if (base.TryGetValue(key1, out var dictionary))
                {
                    dictionary[key2] = value;
                }
                else
                {
                    dictionary = new ListDictionary<TKey2, TValue> { [key2] = value };
                    base.Add(key1, dictionary);
                }
            }
        }

        /// <summary>向指定列表追加值；缺失时创建分组和列表，重复值规则由内层 ListDictionary 决定。</summary>
        public void Add(TKey1 key1, TKey2 key2, TValue value)
        {
            ValidateKeys(key1, key2);
            if (base.TryGetValue(key1, out var dictionary))
            {
                dictionary.Add(key2, value);
                return;
            }

            // 内层添加成功后再发布分组，避免失败时留下空分组。
            dictionary = new ListDictionary<TKey2, TValue>();
            dictionary.Add(key2, value);
            base.Add(key1, dictionary);
        }

        /// <summary>判断指定的双键组合是否存在。</summary>
        public bool ContainsKey(TKey1 key1, TKey2 key2)
        {
            return TryGetValue(key1, key2, out _);
        }

        /// <summary>尝试读取列表；任一键不存在时返回 false，values 为 null。</summary>
        public bool TryGetValue(TKey1 key1, TKey2 key2, [MaybeNullWhen(false)] out List<TValue> values)
        {
            ValidateKeys(key1, key2);
            values = default;
            return base.TryGetValue(key1, out var dictionary) && dictionary.TryGetValue(key2, out values);
        }

        /// <summary>移除整个列表；仅在实际移除时返回 true，并清理被删空的外层分组。</summary>
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

        /// <summary>移除列表中第一次匹配的值；仅在实际移除时返回 true，并逐层清理被删空的分组。</summary>
        public bool Remove(TKey1 key1, TKey2 key2, TValue value)
        {
            ValidateKeys(key1, key2);
            if (!base.TryGetValue(key1, out var dictionary) || !dictionary.Remove(key2, value))
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
