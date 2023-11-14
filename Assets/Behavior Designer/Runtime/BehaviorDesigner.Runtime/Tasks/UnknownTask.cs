// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.Tasks.UnknownTask
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System.Collections.Generic;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks
{
  public class UnknownTask : Task
  {
    [HideInInspector]
    public string JSONSerialization;
    [HideInInspector]
    public List<int> fieldNameHash = new List<int>();
    [HideInInspector]
    public List<int> startIndex = new List<int>();
    [HideInInspector]
    public List<int> dataPosition = new List<int>();
    [HideInInspector]
    public List<Object> unityObjects = new List<Object>();
    [HideInInspector]
    public List<byte> byteData = new List<byte>();
  }
}
