// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.TaskCoroutine
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System.Collections;
using System.Diagnostics;
using UnityEngine;

namespace BehaviorDesigner.Runtime
{
  
  public class TaskCoroutine
  {
    private IEnumerator mCoroutineEnumerator;
    private Coroutine mCoroutine;
    private Behavior mParent;
    private string mCoroutineName;
    private bool mStop;

    public TaskCoroutine(Behavior parent, IEnumerator coroutine, string coroutineName)
    {
      this.mParent = parent;
      this.mCoroutineEnumerator = coroutine;
      this.mCoroutineName = coroutineName;
      this.mCoroutine = parent.StartCoroutine(this.RunCoroutine());
    }

    public Coroutine Coroutine => this.mCoroutine;

    public void Stop() => this.mStop = true;

    [DebuggerHidden]
    public IEnumerator RunCoroutine()
    {
      while (!this.mStop && this.mCoroutineEnumerator.MoveNext())
      {
        yield return this.mCoroutineEnumerator.Current;
      }

      this.mParent.TaskCoroutineEnded(this, this.mCoroutineName);
      yield break;
    }
  }
}
