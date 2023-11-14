// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.IVariableSource
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System.Collections.Generic;

namespace BehaviorDesigner.Runtime
{
  public interface IVariableSource
  {
    SharedVariable GetVariable(string name);

    List<SharedVariable> GetAllVariables();

    void SetVariable(string name, SharedVariable sharedVariable);

    void UpdateVariableName(SharedVariable sharedVariable, string name);

    void SetAllVariables(List<SharedVariable> variables);
  }
}
