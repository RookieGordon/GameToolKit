// Decompiled with JetBrains decompiler
// Type: SharedGenericVariableDrawer
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Editor;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

[CustomObjectDrawer(typeof(GenericVariable))]
public class SharedGenericVariableDrawer : ObjectDrawer
{
    private static string[] variableNames;

    public override void OnGUI(GUIContent label)
    {
        GenericVariable genericVariable = this.value as GenericVariable;
        EditorGUILayout.BeginVertical(Array.Empty<GUILayoutOption>());
        if (FieldInspector.DrawFoldout(((object)genericVariable).GetHashCode(), label))
        {
            ++EditorGUI.indentLevel;
            if (SharedGenericVariableDrawer.variableNames == null)
            {
                List<System.Type> sharedVariableTypes = VariableInspector.FindAllSharedVariableTypes(true);
                SharedGenericVariableDrawer.variableNames = new string[sharedVariableTypes.Count];
                for (int index = 0; index < sharedVariableTypes.Count; ++index)
                    SharedGenericVariableDrawer.variableNames[index] = sharedVariableTypes[index].Name.Remove(0, 6);
            }

            int index1 = 0;
            string str = genericVariable.type.Remove(0, 6);
            for (int index2 = 0; index2 < SharedGenericVariableDrawer.variableNames.Length; ++index2)
            {
                if (SharedGenericVariableDrawer.variableNames[index2].Equals(str))
                {
                    index1 = index2;
                    break;
                }
            }

            int index3 = EditorGUILayout.Popup("Type", index1, SharedGenericVariableDrawer.variableNames, BehaviorDesignerUtility.SharedVariableToolbarPopup, Array.Empty<GUILayoutOption>());
            System.Type sharedVariableType = VariableInspector.FindAllSharedVariableTypes(true)[index3];
            if (index3 != index1)
            {
                index1 = index3;
                genericVariable.value = Activator.CreateInstance(sharedVariableType) as SharedVariable;
            }

            GUILayout.Space(3f);
            genericVariable.type = "Shared" + SharedGenericVariableDrawer.variableNames[index1];
            genericVariable.value = FieldInspector.DrawSharedVariable((Task)null, new GUIContent("Value"), (FieldInfo)null, sharedVariableType, genericVariable.value);
            --EditorGUI.indentLevel;
        }

        EditorGUILayout.EndVertical();
    }
}