// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.Tasks.Composite
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks
{
  public abstract class Composite : ParentTask
  {
    [Tooltip("Specifies the type of conditional abort. More information is located at https://www.opsive.com/support/documentation/behavior-designer/conditional-aborts/.")]
    [SerializeField]
    protected AbortType abortType;

    public AbortType AbortType => this.abortType;

    public virtual bool OnReevaluationStarted() => false;

    public virtual void OnReevaluationEnded(TaskStatus status)
    {
    }
  }
}
