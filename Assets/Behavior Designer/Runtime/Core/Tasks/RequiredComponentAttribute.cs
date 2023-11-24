// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.Tasks.RequiredComponentAttribute
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;

namespace BehaviorDesigner.Runtime.Tasks
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public class RequiredComponentAttribute : Attribute
    {
        public readonly Type mComponentType;

        public RequiredComponentAttribute(Type componentType) => this.mComponentType = componentType;

        public Type ComponentType => this.mComponentType;
    }
}