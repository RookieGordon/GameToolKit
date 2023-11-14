// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.GenericVariable
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using UnityEngine;

namespace BehaviorDesigner.Runtime
{
  [Serializable]
  public class GenericVariable
  {
    [SerializeField]
    public string type = "SharedString";
    [SerializeField]
    public SharedVariable value;

    public GenericVariable() => this.value = Activator.CreateInstance(TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.SharedString")) as SharedVariable;
  }
}
