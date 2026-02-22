/*
 * 功能描述：编辑器GUILayout辅助类
 */

using System;
using UnityEditor;

namespace UnityToolKit.Editor.Common
{
    /// <summary>
    /// EditorGUILayout.BeginVertical / EndVertical 的 IDisposable 包装
    /// </summary>
    public class EditorGUILayoutVertical : IDisposable
    {
        public EditorGUILayoutVertical(params UnityEngine.GUILayoutOption[] options)
        {
            EditorGUILayout.BeginVertical(options);
        }

        public void Dispose()
        {
            EditorGUILayout.EndVertical();
        }
    }

    /// <summary>
    /// EditorGUILayout.BeginHorizontal / EndHorizontal 的 IDisposable 包装
    /// </summary>
    public class EditorGUILayoutHorizontal : IDisposable
    {
        public EditorGUILayoutHorizontal(params UnityEngine.GUILayoutOption[] options)
        {
            EditorGUILayout.BeginHorizontal(options);
        }

        public void Dispose()
        {
            EditorGUILayout.EndHorizontal();
        }
    }
}
