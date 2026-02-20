/* 
****************************************************
* 作者：Gordon
* 创建时间：2025/06/22 16:20:19
* 功能描述：枚举拓展类
****************************************************
*/

using System;
using System.Collections.Generic;

namespace ToolKit.Tools.Extension
{
    public static class EnumExtension
    {
        private static class EnumStorage<T> where T : Enum
        {
            /// <summary>
            /// 枚举中的非法定义对象（枚举值为-1，定义为非法）
            /// </summary>
            public static readonly T Invalid;
            public static readonly T[] Values;
            /// <summary>
            /// 枚举对象对应的索引
            /// </summary>
            public static readonly Dictionary<T, int> EnumToIndexDic = new Dictionary<T, int>();
            /// <summary>
            /// 枚举对象对应的枚举值
            /// </summary>
            public static readonly Dictionary<T, int> EnumToValueDic = new Dictionary<T, int>();
            /// <summary>
            /// 枚举值对应枚举对象
            /// </summary>
            public static readonly Dictionary<int, T> ValueToEnumDic = new Dictionary<int, T>();

            static EnumStorage()
            {
                List<T> values = new List<T>();
                int index = 0;
                foreach (var value in Enum.GetValues(typeof(T)))
                {
                    var enumValue = Convert.ToInt32(value);
                    var enumObj = (T)value;
                    if (enumValue == -1)
                    {
                        Invalid = enumObj;
                        continue;
                    }

                    values.Add(enumObj);
                    ValueToEnumDic.Add(enumValue, enumObj);
                    EnumToValueDic.Add(enumObj, enumValue);
                    EnumToIndexDic.Add(enumObj, index++);
                }

                Values = values.ToArray();
            }
        }

        /// <summary>
        /// 枚举中的非法定义（枚举值为-1）
        /// </summary>
        public static T GetInvalid<T>() where T : Enum => EnumStorage<T>.Invalid;
        /// <summary>
        /// 根据枚举对象，获取其索引
        /// </summary>
        public static int GetIndex<T>(T enumValue) where T : Enum => EnumStorage<T>.EnumToIndexDic[enumValue];
        /// <summary>
        /// 枚举中，所有对象的列表集合
        /// </summary>
        public static T[] GetEnums<T>() where T : Enum => EnumStorage<T>.Values;
    }
}