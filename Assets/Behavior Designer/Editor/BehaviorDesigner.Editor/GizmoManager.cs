// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.GizmoManager
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BehaviorDesigner.Editor
{
  [InitializeOnLoad]
  public class GizmoManager
  {
    private static string currentScene = SceneManager.GetActiveScene().name;

    static GizmoManager()
    {
      EditorApplication.hierarchyChanged += HierarchyChange;
      if (!Application.isPlaying)
      {
        UpdateAllGizmos();
        EditorApplication.playModeStateChanged += UpdateAllGizmos;
      }
    }

    public static void UpdateAllGizmos(PlayModeStateChange change)
    {
      UpdateAllGizmos();
    }

    public static void UpdateAllGizmos()
    {
      Behavior[] behaviorArray = UnityEngine.Object.FindObjectsOfType<Behavior>();
      foreach (Behavior behavior in behaviorArray)
      {
        UpdateGizmo(behavior);
      }
    }

    public static void UpdateGizmo(Behavior behavior)
    {
      behavior.gizmoViewMode = (Behavior.GizmoViewMode)BehaviorDesignerPreferences.GetInt(BDPreferences.GizmosViewMode);
      behavior.showBehaviorDesignerGizmo = BehaviorDesignerPreferences.GetBool(BDPreferences.ShowSceneIcon);
    }

    public static void HierarchyChange()
    {
      if (!Application.isPlaying)
      {
        string name = SceneManager.GetActiveScene().name;
        if (currentScene != name)
        {
          currentScene = name;
          UpdateAllGizmos();
        }
      }
      else
      {
        BehaviorManager instance = BehaviorManager.instance;
        if (instance != null)
        {
          instance.onEnableBehavior += UpdateBehaviorManagerGizmos;
        }
      }
    }

    private static void UpdateBehaviorManagerGizmos()
    {
      BehaviorManager instance = BehaviorManager.instance;
      if (!((UnityEngine.Object)instance != (UnityEngine.Object)null))
      {
        return;
      }
      for (int index = 0; index < instance.BehaviorTrees.Count; ++index)
      {
        UpdateGizmo(instance.BehaviorTrees[index].behavior);
      }
    }
  }
}