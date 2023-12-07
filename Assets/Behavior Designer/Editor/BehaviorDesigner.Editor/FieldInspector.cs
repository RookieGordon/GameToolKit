// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.FieldInspector
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using TooltipAttribute = BehaviorDesigner.Runtime.Tasks.TooltipAttribute;

namespace BehaviorDesigner.Editor
{
    public static class FieldInspector
    {
        private const string c_EditorPrefsFoldoutKey = "BehaviorDesigner.Editor.Foldout.";
        private static int currentKeyboardControl = -1;
        private static bool editingArray = false;
        private static int savedArraySize = -1;
        private static int editingFieldHash;
        public static BehaviorSource behaviorSource;
        private static HashSet<int> drawnObjects = new HashSet<int>();
        private static string[] layerNames;
        private static int[] maskValues;

        public static void Init() => FieldInspector.InitLayers();

        public static bool DrawFoldout(int hash, GUIContent guiContent)
        {
            string str = "BehaviorDesigner.Editor.Foldout.." + (object)hash + "." + guiContent.text;
            bool flag1 = EditorPrefs.GetBool(str, true);
            bool flag2 = EditorGUILayout.Foldout(flag1, guiContent);
            if (flag2 != flag1)
                EditorPrefs.SetBool(str, flag2);
            return flag2;
        }

        public static object DrawFields(Task task, object obj) => FieldInspector.DrawFields(task, obj, (GUIContent)null);

        public static object DrawFields(Task task, object obj, GUIContent guiContent)
        {
            if (obj == null)
                return (object)null;
            List<System.Type> baseClasses = FieldInspector.GetBaseClasses(obj.GetType());
            BindingFlags bindingAttr = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (int index1 = baseClasses.Count - 1; index1 > -1; --index1)
            {
                FieldInfo[] fields = baseClasses[index1].GetFields(bindingAttr);
                for (int index2 = 0; index2 < fields.Length; ++index2)
                {
                    if (!BehaviorDesignerUtility.HasAttribute(fields[index2], typeof(NonSerializedAttribute)) && !BehaviorDesignerUtility.HasAttribute(fields[index2], typeof(HideInInspector)) &&
                        (!fields[index2].IsPrivate && !fields[index2].IsFamily || BehaviorDesignerUtility.HasAttribute(fields[index2], typeof(SerializeField))) && (!(obj is ParentTask) || !fields[index2].Name.Equals("children")))
                    {
                        if (guiContent == null)
                        {
                            string name = fields[index2].Name;
                            TooltipAttribute[] customAttributes;
                            guiContent = (customAttributes = fields[index2].GetCustomAttributes(typeof(TooltipAttribute), false) as TooltipAttribute[]).Length <= 0
                                ? new GUIContent(BehaviorDesignerUtility.SplitCamelCase(name))
                                : new GUIContent(BehaviorDesignerUtility.SplitCamelCase(name), customAttributes[0].Tooltip);
                        }

                        EditorGUI.BeginChangeCheck();
                        object obj1 = FieldInspector.DrawField(task, guiContent, fields[index2], fields[index2].GetValue(obj));
                        if (EditorGUI.EndChangeCheck())
                        {
                            fields[index2].SetValue(obj, obj1);
                            GUI.changed = true;
                        }

                        guiContent = (GUIContent)null;
                    }
                }
            }

            return obj;
        }

        public static List<System.Type> GetBaseClasses(System.Type t)
        {
            List<System.Type> baseClasses = new List<System.Type>();
            for (; t != (System.Type)null && !t.Equals(typeof(ParentTask)) && !t.Equals(typeof(Task)) && !t.Equals(typeof(SharedVariable)); t = t.BaseType)
                baseClasses.Add(t);
            return baseClasses;
        }

        public static object DrawField(
            Task task,
            GUIContent guiContent,
            FieldInfo field,
            object value)
        {
            ObjectDrawer objectDrawer1;
            if ((objectDrawer1 = ObjectDrawerUtility.GetObjectDrawer(task, field)) != null)
            {
                if (value == null && !field.FieldType.IsAbstract)
                    value = !typeof(ScriptableObject).IsAssignableFrom(field.FieldType) ? Activator.CreateInstance(field.FieldType, true) : (object)ScriptableObject.CreateInstance(field.FieldType);
                objectDrawer1.Value = value;
                objectDrawer1.OnGUI(guiContent);
                if (objectDrawer1.Value != value)
                {
                    value = objectDrawer1.Value;
                    GUI.changed = true;
                }

                return value;
            }

            ObjectDrawerAttribute[] customAttributes;
            ObjectDrawer objectDrawer2;
            if ((customAttributes = field.GetCustomAttributes(typeof(ObjectDrawerAttribute), true) as ObjectDrawerAttribute[]).Length <= 0 || (objectDrawer2 = ObjectDrawerUtility.GetObjectDrawer(task, field, customAttributes[0])) == null)
                return FieldInspector.DrawField(task, guiContent, field, field.FieldType, value);
            if (value == null)
                value = !typeof(ScriptableObject).IsAssignableFrom(field.FieldType) ? Activator.CreateInstance(field.FieldType, true) : (object)ScriptableObject.CreateInstance(field.FieldType);
            objectDrawer2.Value = value;
            objectDrawer2.OnGUI(guiContent);
            if (objectDrawer2.Value != value)
            {
                value = objectDrawer2.Value;
                GUI.changed = true;
            }

            return value;
        }

        private static object DrawField(
            Task task,
            GUIContent guiContent,
            FieldInfo fieldInfo,
            System.Type fieldType,
            object value)
        {
            return typeof(IList).IsAssignableFrom(fieldType) ? FieldInspector.DrawArrayField(task, guiContent, fieldInfo, fieldType, value) : FieldInspector.DrawSingleField(task, guiContent, fieldInfo, fieldType, value);
        }

        private static object DrawArrayField(
            Task task,
            GUIContent guiContent,
            FieldInfo fieldInfo,
            System.Type fieldType,
            object value)
        {
            System.Type type1;
            if (fieldType.IsArray)
            {
                type1 = fieldType.GetElementType();
            }
            else
            {
                System.Type type2 = fieldType;
                while (!type2.IsGenericType)
                    type2 = type2.BaseType;
                type1 = type2.GetGenericArguments()[0];
            }

            IList list;
            if (value == null)
            {
                if (fieldType.IsGenericType || fieldType.IsArray)
                    list = Activator.CreateInstance(typeof(List<>).MakeGenericType(type1), true) as IList;
                else
                    list = Activator.CreateInstance(fieldType, true) as IList;
                if (fieldType.IsArray)
                {
                    Array instance = Array.CreateInstance(type1, list.Count);
                    list.CopyTo(instance, 0);
                    list = (IList)instance;
                }

                GUI.changed = true;
            }
            else
                list = (IList)value;

            EditorGUILayout.BeginVertical(Array.Empty<GUILayoutOption>());
            if (FieldInspector.DrawFoldout(guiContent.text.GetHashCode(), guiContent))
            {
                ++EditorGUI.indentLevel;
                bool flag = fieldInfo.GetHashCode() + (value == null ? 0 : value.GetHashCode()) == FieldInspector.editingFieldHash;
                int num = !flag ? list.Count : FieldInspector.savedArraySize;
                int length = EditorGUILayout.IntField("Size", num, Array.Empty<GUILayoutOption>());
                if (flag && FieldInspector.editingArray && (GUIUtility.keyboardControl != FieldInspector.currentKeyboardControl || Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
                {
                    if (length != list.Count)
                    {
                        Array instance1 = Array.CreateInstance(type1, length);
                        int index1 = -1;
                        for (int index2 = 0; index2 < length; ++index2)
                        {
                            if (index2 < list.Count)
                                index1 = index2;
                            if (index1 != -1)
                            {
                                object instance2 = list[index1];
                                if (index2 >= list.Count && !typeof(UnityEngine.Object).IsAssignableFrom(type1) && !typeof(string).IsAssignableFrom(type1))
                                    instance2 = Activator.CreateInstance(list[index1].GetType(), true);
                                instance1.SetValue(instance2, index2);
                            }
                            else
                                break;
                        }

                        if (fieldType.IsArray)
                        {
                            list = (IList)instance1;
                        }
                        else
                        {
                            if (fieldType.IsGenericType)
                                list = Activator.CreateInstance(typeof(List<>).MakeGenericType(type1), true) as IList;
                            else
                                list = Activator.CreateInstance(fieldType, true) as IList;
                            for (int index3 = 0; index3 < instance1.Length; ++index3)
                                list.Add(instance1.GetValue(index3));
                        }
                    }

                    FieldInspector.editingArray = false;
                    FieldInspector.savedArraySize = -1;
                    FieldInspector.editingFieldHash = -1;
                    GUI.changed = true;
                }
                else if (length != num)
                {
                    if (!FieldInspector.editingArray)
                    {
                        FieldInspector.currentKeyboardControl = GUIUtility.keyboardControl;
                        FieldInspector.editingArray = true;
                        FieldInspector.editingFieldHash = fieldInfo.GetHashCode() + (value == null ? 0 : value.GetHashCode());
                    }

                    FieldInspector.savedArraySize = length;
                }

                for (int index = 0; index < list.Count; ++index)
                {
                    GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
                    guiContent.text = "Element " + (object)index;
                    list[index] = FieldInspector.DrawField(task, guiContent, fieldInfo, type1, list[index]);
                    GUILayout.Space(6f);
                    GUILayout.EndHorizontal();
                }

                --EditorGUI.indentLevel;
            }

            EditorGUILayout.EndVertical();
            return (object)list;
        }

        private static object DrawSingleField(
            Task task,
            GUIContent guiContent,
            FieldInfo fieldInfo,
            System.Type fieldType,
            object value)
        {
            if (fieldType.Equals(typeof(int)))
                return (object)EditorGUILayout.IntField(guiContent, (int)value, Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(float)))
                return (object)EditorGUILayout.FloatField(guiContent, (float)value, Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(double)))
                return (object)EditorGUILayout.FloatField(guiContent, Convert.ToSingle((double)value), Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(long)))
                return (object)(long)EditorGUILayout.IntField(guiContent, Convert.ToInt32((long)value), Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(bool)))
                return (object)EditorGUILayout.Toggle(guiContent, (bool)value, Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(string)))
                return (object)EditorGUILayout.TextField(guiContent, (string)value, Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(byte)))
                return (object)Convert.ToByte(EditorGUILayout.IntField(guiContent, Convert.ToInt32(value), Array.Empty<GUILayoutOption>()));
            if (fieldType.Equals(typeof(uint)))
            {
                int num = EditorGUILayout.IntField(guiContent, Convert.ToInt32(value), Array.Empty<GUILayoutOption>());
                if (num < 0)
                    num = 0;
                return (object)Convert.ToUInt32(num);
            }

            if (fieldType.Equals(typeof(ushort)))
            {
                int num = EditorGUILayout.IntField(guiContent, (int)Convert.ToUInt16(value), Array.Empty<GUILayoutOption>());
                if (num < 0)
                    num = 0;
                return (object)Convert.ToUInt16(num);
            }

            if (fieldType.Equals(typeof(Vector2)))
                return (object)EditorGUILayout.Vector2Field(guiContent, (Vector2)value, Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(Vector2Int)))
                return (object)EditorGUILayout.Vector2IntField(guiContent, (Vector2Int)value, Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(Vector3)))
                return (object)EditorGUILayout.Vector3Field(guiContent, (Vector3)value, Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(Vector3Int)))
                return (object)EditorGUILayout.Vector3IntField(guiContent, (Vector3Int)value, Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(Vector3)))
                return (object)EditorGUILayout.Vector3Field(guiContent, (Vector3)value, Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(Vector4)))
                return (object)EditorGUILayout.Vector4Field(guiContent.text, (Vector4)value, Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(Quaternion)))
            {
                Quaternion quaternion = (Quaternion)value;
                Vector4 zero = Vector4.zero;
                zero.Set(quaternion.x, quaternion.y, quaternion.z, quaternion.w);
                Vector4 vector4 = EditorGUILayout.Vector4Field(guiContent.text, zero, Array.Empty<GUILayoutOption>());
                quaternion.Set(vector4.x, vector4.y, vector4.z, vector4.w);
                return (object)quaternion;
            }

            if (fieldType.Equals(typeof(Color)))
                return (object)EditorGUILayout.ColorField(guiContent, (Color)value, Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(Rect)))
                return (object)EditorGUILayout.RectField(guiContent, (Rect)value, Array.Empty<GUILayoutOption>());
            if (fieldType.Equals(typeof(Matrix4x4)))
            {
                GUILayout.BeginVertical(Array.Empty<GUILayoutOption>());
                if (FieldInspector.DrawFoldout(guiContent.text.GetHashCode(), guiContent))
                {
                    ++EditorGUI.indentLevel;
                    Matrix4x4 matrix4x4 = (Matrix4x4)value;
                    for (int row = 0; row < 4; ++row)
                    {
                        for (int column = 0; column < 4; ++column)
                        {
                            EditorGUI.BeginChangeCheck();
                            matrix4x4[row, column] = EditorGUILayout.FloatField("E" + row.ToString() + column.ToString(), matrix4x4[row, column], Array.Empty<GUILayoutOption>());
                            if (EditorGUI.EndChangeCheck())
                                GUI.changed = true;
                        }
                    }

                    value = (object)matrix4x4;
                    --EditorGUI.indentLevel;
                }

                GUILayout.EndVertical();
                return value;
            }

            if (fieldType.Equals(typeof(AnimationCurve)))
            {
                if (value == null)
                {
                    value = (object)AnimationCurve.EaseInOut(0.0f, 0.0f, 1f, 1f);
                    GUI.changed = true;
                }

                return (object)EditorGUILayout.CurveField(guiContent, (AnimationCurve)value, Array.Empty<GUILayoutOption>());
            }

            if (fieldType.Equals(typeof(LayerMask)))
                return (object)FieldInspector.DrawLayerMask(guiContent, (LayerMask)value);
            if (typeof(SharedVariable).IsAssignableFrom(fieldType))
                return (object)FieldInspector.DrawSharedVariable(task, guiContent, fieldInfo, fieldType, value as SharedVariable);
            if (typeof(UnityEngine.Object).IsAssignableFrom(fieldType))
                return (object)EditorGUILayout.ObjectField(guiContent, (UnityEngine.Object)value, fieldType, true, Array.Empty<GUILayoutOption>());
            if (fieldType.IsEnum)
                return fieldType.GetCustomAttribute<FlagsAttribute>() != null ? (object)EditorGUILayout.EnumFlagsField(guiContent, (Enum)value, Array.Empty<GUILayoutOption>()) : (object)EditorGUILayout.EnumPopup(guiContent, (Enum)value, Array.Empty<GUILayoutOption>());
            if (fieldType.IsClass || fieldType.IsValueType && !fieldType.IsPrimitive)
            {
                if (typeof(Delegate).IsAssignableFrom(fieldType))
                    return (object)null;
                int hashCode = guiContent.text.GetHashCode();
                if (FieldInspector.drawnObjects.Contains(hashCode))
                    return (object)null;
                try
                {
                    FieldInspector.drawnObjects.Add(hashCode);
                    GUILayout.BeginVertical(Array.Empty<GUILayoutOption>());
                    if (value == null)
                    {
                        if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(Nullable<>))
                            fieldType = Nullable.GetUnderlyingType(fieldType);
                        value = Activator.CreateInstance(fieldType, true);
                    }

                    if (FieldInspector.DrawFoldout(hashCode, guiContent))
                    {
                        ++EditorGUI.indentLevel;
                        value = FieldInspector.DrawFields(task, value);
                        --EditorGUI.indentLevel;
                    }

                    FieldInspector.drawnObjects.Remove(hashCode);
                    GUILayout.EndVertical();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    GUILayout.EndVertical();
                    FieldInspector.drawnObjects.Remove(hashCode);
                }

                return value;
            }

            EditorGUILayout.LabelField("Unsupported Type: " + (object)fieldType, Array.Empty<GUILayoutOption>());
            return (object)null;
        }

        public static SharedVariable DrawSharedVariable(
            Task task,
            GUIContent guiContent,
            FieldInfo fieldInfo,
            System.Type fieldType,
            SharedVariable sharedVariable)
        {
            if (!fieldType.Equals(typeof(SharedVariable)))
            {
                if (sharedVariable == null)
                {
                    sharedVariable = Activator.CreateInstance(fieldType, true) as SharedVariable;
                    GUI.changed = true;
                }

                if (!sharedVariable.IsShared && (TaskUtility.HasAttribute(fieldInfo, typeof(RequiredFieldAttribute)) || TaskUtility.HasAttribute(fieldInfo, typeof(SharedRequiredAttribute))))
                {
                    sharedVariable.IsShared = true;
                    GUI.changed = true;
                }
            }

            if (sharedVariable != null && sharedVariable.IsDynamic)
            {
                sharedVariable.Name = EditorGUILayout.TextField(guiContent, sharedVariable.Name, Array.Empty<GUILayoutOption>());
                sharedVariable = FieldInspector.DrawSharedVariableToggleSharedButton(sharedVariable);
                if (!sharedVariable.IsDynamic && (TaskUtility.HasAttribute(fieldInfo, typeof(RequiredFieldAttribute)) || TaskUtility.HasAttribute(fieldInfo, typeof(SharedRequiredAttribute))))
                    sharedVariable = (SharedVariable)null;
            }
            else if (sharedVariable == null || sharedVariable.IsShared)
            {
                GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
                string[] names = (string[])null;
                int globalStartIndex = -1;
                bool addDynamic = !fieldType.Equals(typeof(SharedVariable));
                int variablesOfType = FieldInspector.GetVariablesOfType(sharedVariable == null ? (System.Type)null : ((object)sharedVariable).GetType().GetProperty("Value").PropertyType,
                    sharedVariable != null && sharedVariable.IsGlobal,
                    sharedVariable == null ? string.Empty : sharedVariable.Name,
                    FieldInspector.behaviorSource,
                    out names,
                    ref globalStartIndex,
                    fieldType.Equals(typeof(SharedVariable)),
                    addDynamic);
                Color backgroundColor = GUI.backgroundColor;
                if (variablesOfType == 0 && !TaskUtility.HasAttribute(fieldInfo, typeof(SharedRequiredAttribute)))
                    GUI.backgroundColor = Color.red;
                int num = variablesOfType;
                int index = EditorGUILayout.Popup(guiContent.text, variablesOfType, names, BehaviorDesignerUtility.SharedVariableToolbarPopup, Array.Empty<GUILayoutOption>());
                GUI.backgroundColor = backgroundColor;
                if (index != num)
                {
                    if (index == 0)
                    {
                        if (fieldType.Equals(typeof(SharedVariable)))
                        {
                            sharedVariable = (SharedVariable)null;
                        }
                        else
                        {
                            sharedVariable = Activator.CreateInstance(fieldType, true) as SharedVariable;
                            sharedVariable.IsShared = true;
                        }
                    }
                    else if (index < names.Length - (!addDynamic ? 0 : 1))
                    {
                        sharedVariable = globalStartIndex == -1 || index < globalStartIndex ? FieldInspector.behaviorSource.GetVariable(names[index]) : GlobalVariables.Instance.GetVariable(names[index].Substring(8, names[index].Length - 8));
                    }
                    else
                    {
                        sharedVariable = Activator.CreateInstance(fieldType, true) as SharedVariable;
                        sharedVariable.IsShared = true;
                        sharedVariable.IsDynamic = true;
                    }

                    GUI.changed = true;
                }

                if (!fieldType.Equals(typeof(SharedVariable)) && !TaskUtility.HasAttribute(fieldInfo, typeof(RequiredFieldAttribute)) && !TaskUtility.HasAttribute(fieldInfo, typeof(SharedRequiredAttribute)))
                {
                    sharedVariable = FieldInspector.DrawSharedVariableToggleSharedButton(sharedVariable);
                    GUILayout.Space(-3f);
                }

                GUILayout.EndHorizontal();
                GUILayout.Space(3f);
            }
            else
            {
                GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
                ObjectDrawerAttribute[] customAttributes;
                ObjectDrawer objectDrawer;
                if (fieldInfo != (FieldInfo)null && (customAttributes = fieldInfo.GetCustomAttributes(typeof(ObjectDrawerAttribute), true) as ObjectDrawerAttribute[]).Length > 0 && (objectDrawer = ObjectDrawerUtility.GetObjectDrawer(task, fieldInfo, customAttributes[0])) != null)
                {
                    objectDrawer.Value = (object)sharedVariable;
                    objectDrawer.OnGUI(guiContent);
                }
                else
                    FieldInspector.DrawFields(task, (object)sharedVariable, guiContent);

                if (!TaskUtility.HasAttribute(fieldInfo, typeof(RequiredFieldAttribute)) && !TaskUtility.HasAttribute(fieldInfo, typeof(SharedRequiredAttribute)))
                    sharedVariable = FieldInspector.DrawSharedVariableToggleSharedButton(sharedVariable);
                GUILayout.EndHorizontal();
            }

            return sharedVariable;
        }

        public static int GetVariablesOfType(
            System.Type valueType,
            bool isGlobal,
            string name,
            BehaviorSource behaviorSource,
            out string[] names,
            ref int globalStartIndex,
            bool getAll,
            bool addDynamic)
        {
            if (behaviorSource == null)
            {
                names = new string[0];
                return 0;
            }

            List<SharedVariable> variables1 = behaviorSource.Variables;
            int variablesOfType = 0;
            List<string> stringList = new List<string>();
            stringList.Add("(None)");
            if (variables1 != null)
            {
                for (int index = 0; index < variables1.Count; ++index)
                {
                    if (variables1[index] != null)
                    {
                        System.Type propertyType = ((object)variables1[index]).GetType().GetProperty("Value").PropertyType;
                        if (valueType == (System.Type)null || getAll || valueType.IsAssignableFrom(propertyType))
                        {
                            stringList.Add(variables1[index].Name);
                            if (!isGlobal && variables1[index].Name.Equals(name))
                                variablesOfType = stringList.Count - 1;
                        }
                    }
                }
            }

            GlobalVariables instance;
            if ((UnityEngine.Object)(instance = GlobalVariables.Instance) != (UnityEngine.Object)null)
            {
                globalStartIndex = stringList.Count;
                List<SharedVariable> variables2 = instance.Variables;
                if (variables2 != null)
                {
                    for (int index = 0; index < variables2.Count; ++index)
                    {
                        if (variables2[index] != null)
                        {
                            System.Type propertyType = ((object)variables2[index]).GetType().GetProperty("Value").PropertyType;
                            if (valueType == (System.Type)null || getAll || propertyType.Equals(valueType))
                            {
                                stringList.Add("Globals/" + variables2[index].Name);
                                if (isGlobal && variables2[index].Name.Equals(name))
                                    variablesOfType = stringList.Count - 1;
                            }
                        }
                    }
                }
            }

            if (addDynamic)
                stringList.Add("(Dynamic)");
            names = stringList.ToArray();
            return variablesOfType;
        }

        internal static SharedVariable DrawSharedVariableToggleSharedButton(
            SharedVariable sharedVariable)
        {
            if (sharedVariable == null)
                return (SharedVariable)null;
            if (GUILayout.Button((Texture)(!sharedVariable.IsShared ? BehaviorDesignerUtility.VariableButtonTexture : BehaviorDesignerUtility.VariableButtonSelectedTexture),
                    BehaviorDesignerUtility.PlainButtonGUIStyle,
                    new GUILayoutOption[1]
                    {
                        GUILayout.Width(15f)
                    }))
            {
                bool flag = !sharedVariable.IsShared;
                sharedVariable = !((object)sharedVariable).GetType().Equals(typeof(SharedVariable))
                    ? Activator.CreateInstance(((object)sharedVariable).GetType(), true) as SharedVariable
                    : Activator.CreateInstance(FieldInspector.FriendlySharedVariableName(((object)sharedVariable).GetType().GetProperty("Value").PropertyType), true) as SharedVariable;
                sharedVariable.IsShared = flag;
                if (!flag)
                    sharedVariable.IsDynamic = false;
            }

            return sharedVariable;
        }

        internal static System.Type FriendlySharedVariableName(System.Type type)
        {
            if (type.Equals(typeof(bool)))
                return TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.SharedBool");
            if (type.Equals(typeof(int)))
                return TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.SharedInt");
            if (type.Equals(typeof(float)))
                return TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.SharedFloat");
            if (type.Equals(typeof(string)))
                return TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.SharedString");
            System.Type typeWithinAssembly1 = TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.Shared" + type.Name);
            if (typeWithinAssembly1 != (System.Type)null)
                return typeWithinAssembly1;
            System.Type typeWithinAssembly2 = TaskUtility.GetTypeWithinAssembly("Shared" + type.Name);
            return typeWithinAssembly2 != (System.Type)null ? typeWithinAssembly2 : type;
        }

        private static LayerMask DrawLayerMask(GUIContent guiContent, LayerMask layerMask)
        {
            if (FieldInspector.layerNames == null)
                FieldInspector.InitLayers();
            int num1 = 0;
            for (int index = 0; index < FieldInspector.layerNames.Length; ++index)
            {
                if ((layerMask.value & FieldInspector.maskValues[index]) == FieldInspector.maskValues[index])
                    num1 |= 1 << index;
            }

            int num2 = EditorGUILayout.MaskField(guiContent, num1, FieldInspector.layerNames, Array.Empty<GUILayoutOption>());
            if (num2 != num1)
            {
                int num3 = 0;
                for (int index = 0; index < FieldInspector.layerNames.Length; ++index)
                {
                    if ((num2 & 1 << index) != 0)
                        num3 |= FieldInspector.maskValues[index];
                }

                layerMask.value = num3;
            }

            return layerMask;
        }

        private static void InitLayers()
        {
            List<string> stringList = new List<string>();
            List<int> intList = new List<int>();
            for (int layer = 0; layer < 32; ++layer)
            {
                string name = LayerMask.LayerToName(layer);
                if (!string.IsNullOrEmpty(name))
                {
                    stringList.Add(name);
                    intList.Add(1 << layer);
                }
            }

            FieldInspector.layerNames = stringList.ToArray();
            FieldInspector.maskValues = intList.ToArray();
        }
    }
}