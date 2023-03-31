using System.Collections.Generic;
using UnityEngine;

namespace Bonsai.Core
{
    public partial class Blackboard : ScriptableObject, ISerializationCallbackReceiver
    {
        // Used to serailize the key names.
        // Note: Cannot be readonly since it will not serialize in the ScriptableObject.
        [SerializeField, HideInInspector]
#pragma warning disable IDE0044 // Add readonly modifier
        private List<string> _keys = new List<string>();
#pragma warning restore IDE0044 // Add readonly modifier
        
        /// <summary>
        /// Sets all Blackboard keys with unset values.
        /// </summary>
        public void OnAfterDeserialize()
        {
            this._memory.Clear();

            foreach (string key in this._keys)
            {
                this._memory.Add(key, null);
            }
        }

        /// <summary>
        /// Collects all current Blackboard keys for serialization as a List.
        /// </summary>
        public void OnBeforeSerialize()
        {
            this._keys.Clear();
            foreach (string key in this._memory.Keys)
            {
                this._keys.Add(key);
            }
        }
    }
}