// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.CustomObjectDrawer
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using System;

namespace BehaviorDesigner.Editor
{
  [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
  public sealed class CustomObjectDrawer : Attribute
  {
    private Type type;

    public CustomObjectDrawer(Type type) => this.type = type;

    public Type Type => this.type;
  }
}
