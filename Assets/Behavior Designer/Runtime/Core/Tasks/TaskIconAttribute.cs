// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.Tasks.TaskIconAttribute
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;

namespace BehaviorDesigner.Runtime.Tasks
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public class TaskIconAttribute : Attribute
    {
        private string mIconPath;
        private string mLightIconGUID;
        private string mDarkIconGUID;

        public TaskIconAttribute(string iconString)
        {
            if (iconString.ToLower().Contains("."))
            {
                this.mIconPath = iconString;
            }
            else
            {
                this.mLightIconGUID = this.mDarkIconGUID = iconString;
            }
        }

        public TaskIconAttribute(string lightIconGUID, string darkIconGUID)
        {
            this.mLightIconGUID = lightIconGUID;
            this.mDarkIconGUID = darkIconGUID;
        }

        public string IconPath
        {
            get => this.mIconPath;
            set => this.mIconPath = value;
        }

        public string LightIconGUID => this.mLightIconGUID;

        public string DarkIconGUID => this.mDarkIconGUID;
    }
}