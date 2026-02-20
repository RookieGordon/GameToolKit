/*
 ****************************************************
 * 作者：Gordon
 * 创建时间：2025/06/22 16:26:05
 * 功能描述：集合拓展类
 ****************************************************
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ToolKit.Tools.Extension
{
    public static class CollectionExtension
    {
        #region IEnumerable

        public static string ToString<T>(this IEnumerable<T> collections, char breakAppend,
            Func<T, string> onEachAppend = null)
        {
            StringBuilder builder = new StringBuilder();
            int maxIndex = collections.Count() - 1;
            int curIndex = 0;
            foreach (var element in collections)
            {
                builder.Append(onEachAppend != null ? onEachAppend(element) : element.ToString());
                if (curIndex != maxIndex)
                {
                    builder.Append(breakAppend);
                }

                curIndex++;
            }

            return builder.ToString();
        }

        public static IEnumerable<T> Collect<T>(this IEnumerable<T> _collection, Predicate<T> _predicate)
        {
            foreach (T element in _collection)
            {
                if (!_predicate(element))
                    continue;
                yield return element;
            }
        }

        #endregion

        #region List

        public static bool TryAdd<T>(this List<T> list, T element)
        {
            if (list.Contains(element))
            {
                return false;
            }

            list.Add(element);
            return true;
        }

        #endregion
    }
}