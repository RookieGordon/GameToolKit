﻿// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.GlobalVariablesInspector
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using System;
using UnityEditor;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
    [CustomEditor(typeof(GlobalVariables))]
    public class GlobalVariablesInspector : UnityEditor.Editor
    {
        public virtual void OnInspectorGUI()
        {
            if (!GUILayout.Button("Open Global Variabes", Array.Empty<GUILayoutOption>()))
                return;
            GlobalVariablesWindow.ShowWindow();
        }
    }
}