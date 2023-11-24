// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.TaskUtility
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections.Generic;
using System.Reflection;
using BehaviorDesigner.Runtime.Tasks;
#if !UNITY_EDITOR
using SerializeField = Newtonsoft.Json.JsonPropertyAttribute;
#else
using UnityEngine;
#endif

namespace BehaviorDesigner.Runtime
{
    public class TaskUtility
    {
        public static char[] TrimCharacters = new char[1] { '/' };

        private static Dictionary<string, System.Type> typeLookup = new Dictionary<string, System.Type>();

        private static List<Assembly> loadedAssemblies = (List<Assembly>)null;

        private static Dictionary<System.Type, FieldInfo[]> allFieldsLookup =
            new Dictionary<System.Type, FieldInfo[]>();

        private static Dictionary<System.Type, FieldInfo[]> serializableFieldsLookup =
            new Dictionary<System.Type, FieldInfo[]>();

        private static Dictionary<System.Type, FieldInfo[]> publicFieldsLookup =
            new Dictionary<System.Type, FieldInfo[]>();

        private static Dictionary<FieldInfo, Dictionary<System.Type, bool>> hasFieldLookup =
            new Dictionary<FieldInfo, Dictionary<System.Type, bool>>();

        public static object CreateInstance(System.Type t)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                t = Nullable.GetUnderlyingType(t);
            }

            return Activator.CreateInstance(t, true);
        }

        public static FieldInfo[] GetAllFields(System.Type t)
        {
            FieldInfo[] allFields = (FieldInfo[])null;
            if (!TaskUtility.allFieldsLookup.TryGetValue(t, out allFields))
            {
                List<FieldInfo> fieldList = ObjectPool.Get<List<FieldInfo>>();
                fieldList.Clear();
                BindingFlags flags =
                    BindingFlags.DeclaredOnly
                    | BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic;
                TaskUtility.GetFields(t, ref fieldList, (int)flags);
                allFields = fieldList.ToArray();
                ObjectPool.Return<List<FieldInfo>>(fieldList);
                TaskUtility.allFieldsLookup.Add(t, allFields);
            }

            return allFields;
        }

        public static FieldInfo[] GetPublicFields(System.Type t)
        {
            FieldInfo[] publicFields = (FieldInfo[])null;
            if (!TaskUtility.publicFieldsLookup.TryGetValue(t, out publicFields))
            {
                List<FieldInfo> fieldList = ObjectPool.Get<List<FieldInfo>>();
                fieldList.Clear();
                BindingFlags flags =
                    BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public;
                TaskUtility.GetFields(t, ref fieldList, (int)flags);
                publicFields = fieldList.ToArray();
                ObjectPool.Return<List<FieldInfo>>(fieldList);
                TaskUtility.publicFieldsLookup.Add(t, publicFields);
            }

            return publicFields;
        }

        public static FieldInfo[] GetSerializableFields(System.Type t)
        {
            FieldInfo[] serializableFields = (FieldInfo[])null;
            if (!TaskUtility.serializableFieldsLookup.TryGetValue(t, out serializableFields))
            {
                List<FieldInfo> fieldList = ObjectPool.Get<List<FieldInfo>>();
                fieldList.Clear();
                BindingFlags flags =
                    BindingFlags.DeclaredOnly
                    | BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic;
                TaskUtility.GetSerializableFields(t, (IList<FieldInfo>)fieldList, (int)flags);
                serializableFields = fieldList.ToArray();
                ObjectPool.Return<List<FieldInfo>>(fieldList);
                TaskUtility.serializableFieldsLookup.Add(t, serializableFields);
            }

            return serializableFields;
        }

        private static void GetSerializableFields(
            System.Type t,
            IList<FieldInfo> fieldList,
            int flags
        )
        {
            if (
                t == (System.Type)null
                || t.Equals(typeof(ParentTask))
                || t.Equals(typeof(Task))
                || t.Equals(typeof(SharedVariable))
            )
            {
                return;
            }

            FieldInfo[] fields = t.GetFields((BindingFlags)flags);
            for (int index = 0; index < fields.Length; ++index)
            {
                if (
                    fields[index].IsPublic
                    || TaskUtility.HasAttribute(fields[index], typeof(SerializeField))
                )
                {
                    fieldList.Add(fields[index]);
                }
            }

            TaskUtility.GetSerializableFields(t.BaseType, fieldList, flags);
        }

        private static void GetFields(System.Type t, ref List<FieldInfo> fieldList, int flags)
        {
            if (
                t == (System.Type)null
                || t.Equals(typeof(ParentTask))
                || t.Equals(typeof(Task))
                || t.Equals(typeof(SharedVariable))
            )
            {
                return;
            }

            foreach (FieldInfo field in t.GetFields((BindingFlags)flags))
            {
                fieldList.Add(field);
            }

            TaskUtility.GetFields(t.BaseType, ref fieldList, flags);
        }

        public static System.Type GetTypeWithinAssembly(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return (System.Type)null;
            }

            System.Type type;
            if (TaskUtility.typeLookup.TryGetValue(typeName, out type))
            {
                return type;
            }

            type = System.Type.GetType(typeName);
            if (type == (System.Type)null)
            {
                if (TaskUtility.loadedAssemblies == null)
                {
                    TaskUtility.loadedAssemblies = new List<Assembly>();
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        TaskUtility.loadedAssemblies.Add(assembly);
                    }
                }

                for (int index = 0; index < TaskUtility.loadedAssemblies.Count; ++index)
                {
                    type = TaskUtility.loadedAssemblies[index].GetType(typeName);
                    if (type != (System.Type)null)
                    {
                        break;
                    }
                }
            }

            if (type != (System.Type)null)
            {
                TaskUtility.typeLookup.Add(typeName, type);
            }
            else if (typeName.Contains("BehaviorDesigner.Runtime.Tasks.Basic"))
            {
                return TaskUtility.GetTypeWithinAssembly(
                    typeName.Replace(
                        "BehaviorDesigner.Runtime.Tasks.Basic",
                        "BehaviorDesigner.Runtime.Tasks.Unity"
                    )
                );
            }

            return type;
        }

        public static bool CompareType(System.Type t, string typeName)
        {
            System.Type typeWithinAssembly = TaskUtility.GetTypeWithinAssembly(typeName);
            return !(typeWithinAssembly == (System.Type)null) && t.Equals(typeWithinAssembly);
        }

        public static bool HasAttribute(FieldInfo field, System.Type attribute)
        {
            if (field == (FieldInfo)null)
            {
                return false;
            }

            Dictionary<System.Type, bool> dictionary;
            if (!TaskUtility.hasFieldLookup.TryGetValue(field, out dictionary))
            {
                dictionary = new Dictionary<System.Type, bool>();
                TaskUtility.hasFieldLookup.Add(field, dictionary);
            }

            bool flag;
            if (!dictionary.TryGetValue(attribute, out flag))
            {
                flag = field.GetCustomAttributes(attribute, false).Length > 0;
                dictionary.Add(attribute, flag);
            }

            return flag;
        }
    }
}