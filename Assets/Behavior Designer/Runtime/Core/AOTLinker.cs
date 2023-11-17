// Decompiled with JetBrains decompiler
// Type: AOTLinker
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using UnityEngine;

public class AOTLinker : MonoBehaviour
{
  public void Linker()
  {
    BehaviorManager.BehaviorTree behaviorTree = new BehaviorManager.BehaviorTree();
    BehaviorManager.BehaviorTree.ConditionalReevaluate conditionalReevaluate = new BehaviorManager.BehaviorTree.ConditionalReevaluate();
    BehaviorManager.TaskAddData taskAddData = new BehaviorManager.TaskAddData();
    BehaviorManager.TaskAddData.OverrideFieldValue overrideFieldValue = new BehaviorManager.TaskAddData.OverrideFieldValue();
    UnknownTask unknownTask = new UnknownTask();
  }
}
