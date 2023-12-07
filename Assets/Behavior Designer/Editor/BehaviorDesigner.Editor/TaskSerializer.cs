// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.TaskSerializer
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using System;
using System.Collections.Generic;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
    [Serializable]
    public class TaskSerializer
    {
        public string serialization;
        public Vector2 offset;
        public List<UnityEngine.Object> unityObjects;
        public List<int> childrenIndex;
    }
}