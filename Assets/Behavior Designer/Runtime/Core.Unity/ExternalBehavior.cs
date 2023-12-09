using UnityEngine;
using System;

namespace BehaviorDesigner.Runtime
{
    [Serializable]
    public abstract partial class ExternalBehavior : ScriptableObject
    {
        [SerializeField] private BehaviorSource mBehaviorSource;

        public void OnEnable()
        {
            this.SetUnityObject();
        }

        public UnityEngine.Object GetObject()
        {
            return (UnityEngine.Object)this;
        }

        public string GetOwnerName()
        {
            return this.name;
        }

        int IBehavior.GetInstanceID()
        {
            return this.GetInstanceID();
        }

        public void SetUnityObject()
        {
            this.externalBehaviorName = this.name;
            this.instanceID = this.GetInstanceID();
        }
    }
}