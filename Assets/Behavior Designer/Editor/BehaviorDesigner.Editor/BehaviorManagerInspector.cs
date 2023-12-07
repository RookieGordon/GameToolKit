// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.BehaviorManagerInspector
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using System;
using UnityEditor;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
    [CustomEditor(typeof(BehaviorManager))]
    public class BehaviorManagerInspector : UnityEditor.Editor
    {
        public virtual void OnInspectorGUI()
        {
            BehaviorManager target = this.target as BehaviorManager;
            target.UpdateInterval = (UpdateIntervalType)EditorGUILayout.EnumPopup("Update Interval", (Enum)(object)target.UpdateInterval, Array.Empty<GUILayoutOption>());
            if (target.UpdateInterval == UpdateIntervalType.SpecifySeconds)
            {
                ++EditorGUI.indentLevel;
                target.UpdateIntervalSeconds = EditorGUILayout.FloatField("Seconds", target.UpdateIntervalSeconds, Array.Empty<GUILayoutOption>());
                --EditorGUI.indentLevel;
            }

            target.ExecutionsPerTick = (BehaviorManager.ExecutionsPerTickType)EditorGUILayout.EnumPopup("Task Execution Type", (Enum)(object)target.ExecutionsPerTick, Array.Empty<GUILayoutOption>());
            if (target.ExecutionsPerTick != BehaviorManager.ExecutionsPerTickType.Count)
                return;
            ++EditorGUI.indentLevel;
            target.MaxTaskExecutionsPerTick = EditorGUILayout.IntField("Max Execution Count", target.MaxTaskExecutionsPerTick, Array.Empty<GUILayoutOption>());
            --EditorGUI.indentLevel;
        }
    }
}