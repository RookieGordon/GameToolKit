// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.AlphanumComparator`1
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using System;
using System.Collections.Generic;
using System.Text;
using Action = BehaviorDesigner.Runtime.Tasks.Action;

namespace BehaviorDesigner.Editor
{
    public class AlphanumComparator<T> : IComparer<T>
    {
        //     public int Compare(T x, T y)
        //     {
        //         string empty1 = string.Empty;
        //         string str1;
        //         if (x.GetType().IsSubclassOf(typeof(Type)))
        //         {
        //             Type t = (object)x as Type;
        //             string str2 = this.TypePrefix(t) + "/";
        //             TaskCategoryAttribute[] customAttributes1;
        //             if ((customAttributes1 = t.GetCustomAttributes(typeof(TaskCategoryAttribute), true) as TaskCategoryAttribute[]).Length > 0)
        //             {
        //                 string str3 = customAttributes1[0].Category.TrimEnd(TaskUtility.TrimCharacters);
        //                 str2 = str2 + str3 + "/";
        //             }

        //             TaskNameAttribute[] customAttributes2;
        //             str1 = (customAttributes2 = t.GetCustomAttributes(typeof(TaskNameAttribute), false) as TaskNameAttribute[]).Length <= 0
        //                 ? str2 + BehaviorDesignerUtility.SplitCamelCase(t.Name.ToString())
        //                 : str2 + customAttributes2[0].Name;
        //         }
        //         else if (x.GetType().IsSubclassOf(typeof(SharedVariable)))
        //         {
        //             string s = x.GetType().Name;
        //             if (s.Length > 6 && s.Substring(0, 6).Equals("Shared"))
        //             {
        //                 s = s.Substring(6, s.Length - 6);
        //             }
        //             str1 = BehaviorDesignerUtility.SplitCamelCase(s);
        //         }
        //         else
        //         {
        //             str1 = BehaviorDesignerUtility.SplitCamelCase(x.ToString());
        //         }

        //         if (str1 == null)
        //         {
        //             return 0;
        //         }
        //         string empty2 = string.Empty;
        //         string str4;
        //         if (y.GetType().IsSubclassOf(typeof(Type)))
        //         {
        //             Type t = (object)y as Type;
        //             string str5 = this.TypePrefix(t) + "/";
        //             TaskCategoryAttribute[] customAttributes3;
        //             if ((customAttributes3 = t.GetCustomAttributes(typeof(TaskCategoryAttribute), true) as TaskCategoryAttribute[]).Length > 0)
        //             {
        //                 string str6 = customAttributes3[0].Category.TrimEnd(TaskUtility.TrimCharacters);
        //                 str5 = str5 + str6 + "/";
        //             }

        //             TaskNameAttribute[] customAttributes4;
        //             str4 = (customAttributes4 = t.GetCustomAttributes(typeof(TaskNameAttribute), false) as TaskNameAttribute[]).Length <= 0
        //                 ? str5 + BehaviorDesignerUtility.SplitCamelCase(t.Name.ToString())
        //                 : str5 + customAttributes4[0].Name;
        //         }
        //         else if (y.GetType().IsSubclassOf(typeof(SharedVariable)))
        //         {
        //             string s = y.GetType().Name;
        //             if (s.Length > 6 && s.Substring(0, 6).Equals("Shared"))
        //             {
        //                 s = s.Substring(6, s.Length - 6);
        //             }
        //             str4 = BehaviorDesignerUtility.SplitCamelCase(s);
        //         }
        //         else
        //         {
        //             str4 = BehaviorDesignerUtility.SplitCamelCase(y.ToString());
        //         }

        //         if (str4 == null)
        //         {
        //             return 0;
        //         }
        //         int length1 = str1.Length;
        //         int length2 = str4.Length;
        //         int index1 = 0;
        //         for (int index2 = 0; index1 < length1 && index2 < length2; ++index2)
        //         {
        //             int num;
        //             if (char.IsDigit(str1[index1]) && char.IsDigit(str1[index2]))
        //             {
        //                 string empty3 = string.Empty;
        //                 for (; index1 < length1 && char.IsDigit(str1[index1]); ++index1)
        //                 {
        //                     empty3 += (string)(object)str1[index1];
        //                 }
        //                 string empty4 = string.Empty;
        //                 for (; index2 < length2 && char.IsDigit(str4[index2]); ++index2)
        //                 {
        //                     empty4 += (string)(object)str4[index2];
        //                 }
        //                 int result1 = 0;
        //                 int.TryParse(empty3, out result1);
        //                 int result2 = 0;
        //                 int.TryParse(empty4, out result2);
        //                 num = result1.CompareTo(result2);
        //             }
        //             else
        //             {
        //                 num = str1[index1].CompareTo(str4[index2]);
        //             }

        //             if (num != 0)
        //             {
        //                 return num;
        //             }
        //             ++index1;
        //         }

        //         return length1 - length2;
        //     }

        public int Compare(T x, T y)
        {
            string str1 = GetString(x);
            string str2 = GetString(y);

            if (str1 == null || str2 == null)
            {
                return 0;
            }

            int length1 = str1.Length;
            int length2 = str2.Length;
            int index1 = 0;

            for (int index2 = 0; index1 < length1 && index2 < length2; ++index2)
            {
                int num;

                if (char.IsDigit(str1[index1]) && char.IsDigit(str2[index2]))
                {
                    string empty3 = GetDigitString(str1, ref index1);
                    string empty4 = GetDigitString(str2, ref index2);

                    int result1 = 0;
                    int.TryParse(empty3, out result1);

                    int result2 = 0;
                    int.TryParse(empty4, out result2);

                    num = result1.CompareTo(result2);
                }
                else
                {
                    num = str1[index1].CompareTo(str2[index2]);
                }

                if (num != 0)
                {
                    return num;
                }

                ++index1;
            }

            return length1 - length2;
        }

        private string GetString(T obj)
        {
            if (obj.GetType().IsSubclassOf(typeof(Type)))
            {
                Type t = (object)obj as Type;
                string typePrefix = TypePrefix(t);
                string category = GetCategory(t);
                string taskName = GetTaskName(t);

                return $"{typePrefix}/{category}/{taskName}";
            }
            else if (obj.GetType().IsSubclassOf(typeof(SharedVariable)))
            {
                string s = obj.GetType().Name;
                if (s.Length > 6 && s.Substring(0, 6).Equals("Shared"))
                {
                    s = s.Substring(6, s.Length - 6);
                }

                return BehaviorDesignerUtility.SplitCamelCase(s);
            }
            else
            {
                return BehaviorDesignerUtility.SplitCamelCase(obj.ToString());
            }
        }

        private string GetDigitString(string str, ref int index)
        {
            StringBuilder sb = new StringBuilder();
            for (; index < str.Length && char.IsDigit(str[index]); ++index)
            {
                sb.Append(str[index]);
            }

            return sb.ToString();
        }

        private string GetCategory(Type t)
        {
            TaskCategoryAttribute[] customAttributes = t.GetCustomAttributes(typeof(TaskCategoryAttribute), true) as TaskCategoryAttribute[];
            if (customAttributes.Length > 0)
            {
                return customAttributes[0].Category.TrimEnd(TaskUtility.TrimCharacters);
            }

            return "";
        }

        private string GetTaskName(Type t)
        {
            TaskNameAttribute[] customAttributes = t.GetCustomAttributes(typeof(TaskNameAttribute), false) as TaskNameAttribute[];
            if (customAttributes.Length > 0)
            {
                return customAttributes[0].Name;
            }

            return BehaviorDesignerUtility.SplitCamelCase(t.Name.ToString());
        }


        private string TypePrefix(Type t)
        {
            if (t.IsSubclassOf(typeof(Action)))
            {
                return "Action";
            }

            if (t.IsSubclassOf(typeof(Composite)))
            {
                return "Composite";
            }

            return t.IsSubclassOf(typeof(Conditional)) ? "Conditional" : "Decorator";
        }
    }
}