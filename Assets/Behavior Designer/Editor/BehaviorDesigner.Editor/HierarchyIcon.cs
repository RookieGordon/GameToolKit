// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.HierarchyIcon
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using System;
using UnityEditor;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
  [InitializeOnLoad]
  public class HierarchyIcon : ScriptableObject
  {
    private static Texture2D icon = AssetDatabase.LoadAssetAtPath("Assets/Gizmos/Behavior Designer Hier Icon.png", typeof (Texture2D)) as Texture2D;

    static HierarchyIcon()
    {
      if (icon != null)
      {
          EditorApplication.hierarchyWindowItemOnGUI += HierarchyWindowItemOnGUI;
      }
    }

    private static void HierarchyWindowItemOnGUI(int instanceID, Rect selectionRect)
    {
      if (!BehaviorDesignerPreferences.GetBool(BDPreferences.ShowHierarchyIcon))
        return;
      GameObject gameObject = EditorUtility.InstanceIDToObject(instanceID) as GameObject;
      if (!((UnityEngine.Object) gameObject != (UnityEngine.Object) null) || !((UnityEngine.Object) gameObject.GetComponent<Behavior>() != (UnityEngine.Object) null))
        return;
      Rect rect = new Rect(selectionRect);
      rect.x = rect.width + (selectionRect.x - 16f);
      rect.width = 16f;
      rect.height = 16f;
      GUI.DrawTexture(rect, (Texture) HierarchyIcon.icon);
    }
  }
}
