// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.Tasks.Task
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System.Collections;
using Newtonsoft.Json;

namespace BehaviorDesigner.Runtime.Tasks
{
    public abstract partial class Task
    {
#if !UNITY_PLATFORM
        [JsonProperty] private NodeData nodeData;

        [JsonProperty] private Behavior owner;

        [JsonProperty] private int id = -1;

        [JsonProperty] private string friendlyName = string.Empty;

        [JsonProperty] private bool instant = true;
#endif

        private int referenceID = -1;

        private bool disabled;

        public virtual void OnAwake()
        {
        }

        public virtual void OnStart()
        {
        }

        public virtual TaskStatus OnUpdate()
        {
            return TaskStatus.Success;
        }

        public virtual void OnLateUpdate()
        {
        }

        public virtual void OnFixedUpdate()
        {
        }

        public virtual void OnEnd()
        {
        }

        public virtual void OnPause(bool paused)
        {
        }

        public virtual void OnConditionalAbort()
        {
        }

        public virtual float GetPriority()
        {
            return 0.0f;
        }

        public virtual float GetUtility()
        {
            return 0.0f;
        }

        public virtual void OnBehaviorRestart()
        {
        }

        public virtual void OnBehaviorComplete()
        {
        }

        public virtual void OnReset()
        {
        }

        public virtual void OnDrawGizmos()
        {
        }

        public virtual string OnDrawNodeText()
        {
            return string.Empty;
        }

        public NodeData NodeData
        {
            get => this.nodeData;
            set => this.nodeData = value;
        }

        public Behavior Owner
        {
            get => this.owner;
            set => this.owner = value;
        }

        public int ID
        {
            get => this.id;
            set => this.id = value;
        }

        public virtual string FriendlyName
        {
            get => this.friendlyName;
            set => this.friendlyName = value;
        }

        public bool IsInstant
        {
            get => this.instant;
            set => this.instant = value;
        }

        public int ReferenceID
        {
            get => this.referenceID;
            set => this.referenceID = value;
        }

        public bool Disabled
        {
            get => this.disabled;
            set => this.disabled = value;
        }
        
#if !UNITY_PLATFORM
        protected System.Object GetDefaultGameObject(System.Object go)
        {
            return null;
        }
#endif
        
#if !UNITY_PLATFORM
        protected System.Object GetDefaultGameObjectComponent(System.Object obj, System.Type type)
        {
            return null;
        }
        
        protected T GetDefaultGameObjectComponent<T>(System.Object obj)
        {
            return default(T);
        }
        
        protected T[] GetDefaultGameObjectComponents<T>(System.Object obj)
        {
            return default(T[]);
        }
#endif
    }
}