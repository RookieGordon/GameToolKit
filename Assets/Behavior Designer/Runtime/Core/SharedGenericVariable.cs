// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.SharedGenericVariable
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;

namespace BehaviorDesigner.Runtime
{
    [Serializable]
    public class SharedGenericVariable : SharedVariable<GenericVariable>
    {
        public SharedGenericVariable()
        {
            this.mValue = new GenericVariable();
        }

        public static implicit operator SharedGenericVariable(GenericVariable value)
        {
            SharedGenericVariable sharedGenericVariable = new SharedGenericVariable();
            sharedGenericVariable.mValue = value;
            return sharedGenericVariable;
        }
    }
}