// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.Tasks.BehaviorReference
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll


namespace BehaviorDesigner.Runtime.Tasks
{
    [TaskDescription("Behavior Reference allows you to run another behavior tree within the current behavior tree.")]
    [HelpURL("https://www.opsive.com/support/documentation/behavior-designer/external-behavior-trees/")]
    [TaskIcon("BehaviorTreeReferenceIcon.png")]
    public abstract partial class BehaviorReference : Action
    {
        [Tooltip("External behavior array that this task should reference")]
        public ExternalBehavior[] externalBehaviors;

        [Tooltip("Any variables that should be set for the specific tree")]
        public SharedNamedVariable[] variables;

#if !UNITY_PLATFORM
        public bool collapsed;
#endif

        public virtual ExternalBehavior[] GetExternalBehaviors()
        {
            return this.externalBehaviors;
        }

        public override void OnReset()
        {
            this.externalBehaviors = null;
        }
    }
}