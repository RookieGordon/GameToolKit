// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.SharedNamedVariable
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;

namespace BehaviorDesigner.Runtime
{
    [Serializable]
    public class SharedNamedVariable : SharedVariable<NamedVariable>
    {
        public SharedNamedVariable()
        {
            this.mValue = new NamedVariable();
        }

        public static implicit operator SharedNamedVariable(NamedVariable value)
        {
            SharedNamedVariable sharedNamedVariable = new SharedNamedVariable();
            sharedNamedVariable.mValue = value;
            return sharedNamedVariable;
        }
    }
}
