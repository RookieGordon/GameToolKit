// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.NamedVariable
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using UnityEngine;

namespace BehaviorDesigner.Runtime
{
  [Serializable]
  public class NamedVariable : GenericVariable
  {
    [SerializeField]
    public string name = string.Empty;
  }
}
