// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.IBehavior
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using UnityEngine;

namespace BehaviorDesigner.Runtime
{
  public interface IBehavior
  {
    string GetOwnerName();

    int GetInstanceID();

    BehaviorSource GetBehaviorSource();

    void SetBehaviorSource(BehaviorSource behaviorSource);

    Object GetObject();

    SharedVariable GetVariable(string name);

    void SetVariable(string name, SharedVariable item);

    void SetVariableValue(string name, object value);
  }
}
