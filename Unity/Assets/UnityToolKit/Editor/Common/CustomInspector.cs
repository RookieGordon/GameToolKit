/*
 * 功能描述：CustomInspector 基类
 *           支持 [InspectorButton] 特性标记的方法在Inspector中显示为按钮
 *           借鉴自 Unity3D-ToolChain_StriteR (EInspectorExtension)
 */

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityToolKit.Engine;

namespace UnityToolKit.Editor.Common
{
    /// <summary>
    /// Inspector按钮方法数据
    /// </summary>
    internal class InspectorButtonData
    {
        public MethodInfo Method;
        public ParameterInfo[] Parameters;
        public object[] ParameterValues;
    }

    /// <summary>
    /// 自定义Inspector基类，支持InspectorButton
    /// </summary>
    [CustomEditor(typeof(MonoBehaviour), editorForChildClasses: true, isFallback = true)]
    [CanEditMultipleObjects]
    public class CustomInspector : UnityEditor.Editor
    {
        private List<InspectorButtonData> _inspectorMethods;

        protected virtual void OnEnable()
        {
            _inspectorMethods = GetInspectorMethods(target);
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            if (_inspectorMethods == null || _inspectorMethods.Count <= 0)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Inspector Buttons", EditorStyles.boldLabel);

            foreach (var data in _inspectorMethods)
            {
                EditorGUILayout.BeginVertical();

                if (data.Parameters.Length > 0)
                {
                    EditorGUILayout.LabelField(data.Method.Name, EditorStyles.boldLabel);
                    for (int i = 0; i < data.Parameters.Length; i++)
                    {
                        data.ParameterValues[i] = DrawParameterField(
                            data.Parameters[i].Name,
                            data.Parameters[i].ParameterType,
                            data.ParameterValues[i]);
                    }
                }

                if (GUILayout.Button(data.Parameters.Length > 0 ? "Execute" : data.Method.Name))
                {
                    foreach (var t in targets)
                    {
                        data.Method.Invoke(t, data.ParameterValues);
                    }
                }

                EditorGUILayout.EndVertical();
            }
        }

        private static List<InspectorButtonData> GetInspectorMethods(Object target)
        {
            var result = new List<InspectorButtonData>();
            if (target == null) return result;

            var type = target.GetType();
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<InspectorButtonAttribute>();
                if (attr == null) continue;

                var parameters = method.GetParameters();
                result.Add(new InspectorButtonData
                {
                    Method = method,
                    Parameters = parameters,
                    ParameterValues = new object[parameters.Length],
                });
            }

            return result;
        }

        private static object DrawParameterField(string name, System.Type type, object value)
        {
            if (type == typeof(int))
                return EditorGUILayout.IntField(name, value is int i ? i : 0);
            if (type == typeof(float))
                return EditorGUILayout.FloatField(name, value is float f ? f : 0f);
            if (type == typeof(string))
                return EditorGUILayout.TextField(name, value as string ?? "");
            if (type == typeof(bool))
                return EditorGUILayout.Toggle(name, value is bool b && b);
            if (type == typeof(Vector3))
                return EditorGUILayout.Vector3Field(name, value is Vector3 v3 ? v3 : Vector3.zero);
            if (type == typeof(Vector2))
                return EditorGUILayout.Vector2Field(name, value is Vector2 v2 ? v2 : Vector2.zero);
            if (typeof(Object).IsAssignableFrom(type))
                return EditorGUILayout.ObjectField(name, value as Object, type, true);

            EditorGUILayout.LabelField(name, $"[Unsupported type: {type.Name}]");
            return value;
        }
    }
}
