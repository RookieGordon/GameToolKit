// Decompiled with JetBrains decompiler
// Type: SharedNamedVariableDrawer
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

[CustomObjectDrawer(typeof(NamedVariable))]
public class SharedNamedVariableDrawer : ObjectDrawer
{
    private static string[] variableNames;

    public override void OnGUI(GUIContent label)
    {
        NamedVariable namedVariable = this.value as NamedVariable;
        EditorGUILayout.BeginVertical(Array.Empty<GUILayoutOption>());
        if (FieldInspector.DrawFoldout(((object)namedVariable).GetHashCode(), label))
        {
            ++EditorGUI.indentLevel;
            if (SharedNamedVariableDrawer.variableNames == null)
            {
                List<System.Type> sharedVariableTypes = VariableInspector.FindAllSharedVariableTypes(true);
                SharedNamedVariableDrawer.variableNames = new string[sharedVariableTypes.Count];
                for (int index = 0; index < sharedVariableTypes.Count; ++index)
                    SharedNamedVariableDrawer.variableNames[index] = sharedVariableTypes[index].Name.Remove(0, 6);
            }

            int index1 = 0;
            string str = ((GenericVariable)namedVariable).type.Remove(0, 6);
            for (int index2 = 0; index2 < SharedNamedVariableDrawer.variableNames.Length; ++index2)
            {
                if (SharedNamedVariableDrawer.variableNames[index2].Equals(str))
                {
                    index1 = index2;
                    break;
                }
            }

            namedVariable.name = EditorGUILayout.TextField("Name", namedVariable.name, Array.Empty<GUILayoutOption>());
            int index3 = EditorGUILayout.Popup("Type", index1, SharedNamedVariableDrawer.variableNames, BehaviorDesignerUtility.SharedVariableToolbarPopup, Array.Empty<GUILayoutOption>());
            System.Type sharedVariableType = VariableInspector.FindAllSharedVariableTypes(true)[index3];
            if (index3 != index1)
            {
                index1 = index3;
                ((GenericVariable)namedVariable).value = Activator.CreateInstance(sharedVariableType) as SharedVariable;
            }

            GUILayout.Space(3f);
            ((GenericVariable)namedVariable).type = "Shared" + SharedNamedVariableDrawer.variableNames[index1];
            ((GenericVariable)namedVariable).value = FieldInspector.DrawSharedVariable((Task)null, new GUIContent("Value"), (FieldInfo)null, sharedVariableType, ((GenericVariable)namedVariable).value);
            --EditorGUI.indentLevel;
        }

        EditorGUILayout.EndVertical();
    }
}