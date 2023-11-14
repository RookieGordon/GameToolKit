// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.ExternalBehaviorInspector
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
  [CustomEditor(typeof (ExternalBehavior))]
  public class ExternalBehaviorInspector : UnityEditor.Editor
  {
    private bool mShowVariables;
    private static List<float> variablePosition;
    private static int selectedVariableIndex = -1;
    private static string selectedVariableName;
    private static int selectedVariableTypeIndex;

    public virtual void OnInspectorGUI()
    {
      ExternalBehavior target = this.target as ExternalBehavior;
      if ((UnityEngine.Object) target == (UnityEngine.Object) null)
        return;
      if (target.BehaviorSource.Owner == null)
        target.BehaviorSource.Owner = (IBehavior) target;
      if (!ExternalBehaviorInspector.DrawInspectorGUI(target.BehaviorSource, true, ref this.mShowVariables))
        return;
      BehaviorDesignerUtility.SetObjectDirty((UnityEngine.Object) target);
    }

    public void Reset()
    {
      ExternalBehavior target = this.target as ExternalBehavior;
      if ((UnityEngine.Object) target == (UnityEngine.Object) null || target.BehaviorSource.Owner != null)
        return;
      target.BehaviorSource.Owner = (IBehavior) target;
    }

    public static bool DrawInspectorGUI(
      BehaviorSource behaviorSource,
      bool fromInspector,
      ref bool showVariables)
    {
      EditorGUI.BeginChangeCheck();
      GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
      EditorGUILayout.LabelField("Behavior Name", new GUILayoutOption[1]
      {
        GUILayout.Width(120f)
      });
      behaviorSource.behaviorName = EditorGUILayout.TextField(behaviorSource.behaviorName, Array.Empty<GUILayoutOption>());
      if (fromInspector && GUILayout.Button("Open", Array.Empty<GUILayoutOption>()))
      {
        BehaviorDesignerWindow.ShowWindow();
        BehaviorDesignerWindow.instance.LoadBehavior(behaviorSource, false, true);
      }
      GUILayout.EndHorizontal();
      EditorGUILayout.LabelField("Behavior Description", Array.Empty<GUILayoutOption>());
      behaviorSource.behaviorDescription = EditorGUILayout.TextArea(behaviorSource.behaviorDescription, new GUILayoutOption[1]
      {
        GUILayout.Height(48f)
      });
      if (fromInspector)
      {
        string str = "BehaviorDesigner.VariablesFoldout." + (object) ((object) behaviorSource).GetHashCode();
        if (showVariables = EditorGUILayout.Foldout(EditorPrefs.GetBool(str, true), "Variables"))
        {
          ++EditorGUI.indentLevel;
          List<SharedVariable> allVariables = behaviorSource.GetAllVariables();
          if (allVariables != null && VariableInspector.DrawAllVariables(false, (IVariableSource) behaviorSource, ref allVariables, false, ref ExternalBehaviorInspector.variablePosition, ref ExternalBehaviorInspector.selectedVariableIndex, ref ExternalBehaviorInspector.selectedVariableName, ref ExternalBehaviorInspector.selectedVariableTypeIndex, true, false))
          {
            if (BehaviorDesignerPreferences.GetBool(BDPreferences.BinarySerialization))
              BinarySerialization.Save(behaviorSource);
            else
              JSONSerialization.Save(behaviorSource);
            return true;
          }
          --EditorGUI.indentLevel;
        }
        EditorPrefs.SetBool(str, showVariables);
      }
      return EditorGUI.EndChangeCheck();
    }

    [OnOpenAsset(0)]
    public static bool ClickAction(int instanceID, int line)
    {
      ExternalBehavior externalBehavior = EditorUtility.InstanceIDToObject(instanceID) as ExternalBehavior;
      if ((UnityEngine.Object) externalBehavior == (UnityEngine.Object) null)
        return false;
      BehaviorDesignerWindow.ShowWindow();
      BehaviorDesignerWindow.instance.LoadBehavior(externalBehavior.BehaviorSource, false, true);
      return true;
    }
  }
}
