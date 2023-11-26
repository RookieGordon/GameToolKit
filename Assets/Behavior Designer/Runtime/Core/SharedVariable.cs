// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.SharedVariable
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using SerializeField = Newtonsoft.Json.JsonPropertyAttribute;

namespace BehaviorDesigner.Runtime
{
    public abstract partial class SharedVariable
    {
      
#if !UNITY_PLATFORM
        [SerializeField]
        private bool mIsShared;

        [SerializeField]
        private bool mIsGlobal;

        [SerializeField]
        private bool mIsDynamic;

        [SerializeField]
        private string mName;

        [SerializeField]
        private string mToolTip;

        [SerializeField]
        private string mPropertyMapping;
#endif

        public bool IsShared
        {
            get => this.mIsShared;
            set => this.mIsShared = value;
        }

        public bool IsGlobal
        {
            get => this.mIsGlobal;
            set => this.mIsGlobal = value;
        }

        public bool IsDynamic
        {
            get => this.mIsDynamic;
            set => this.mIsDynamic = value;
        }

        public string Name
        {
            get => this.mName;
            set => this.mName = value;
        }

        public string Tooltip
        {
            get => this.mToolTip;
            set => this.mToolTip = value;
        }

        public string PropertyMapping
        {
            get => this.mPropertyMapping;
            set => this.mPropertyMapping = value;
        }

        public bool IsNone => this.mIsShared && string.IsNullOrEmpty(this.mName);

        public virtual void InitializePropertyMapping(BehaviorSource behaviorSource) { }

        public abstract object GetValue();

        public abstract void SetValue(object value);
    }
}
