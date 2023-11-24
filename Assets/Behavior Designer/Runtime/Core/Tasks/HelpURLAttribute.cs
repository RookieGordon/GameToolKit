// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.Tasks.HelpURLAttribute
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;

namespace BehaviorDesigner.Runtime.Tasks
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class HelpURLAttribute : Attribute
    {
        private readonly string mURL;

        public HelpURLAttribute(string url) => this.mURL = url;

        public string URL => this.mURL;
    }
}