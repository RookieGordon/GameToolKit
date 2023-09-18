using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Bonsai.Core
{
    ///<summary>
    /// A heterogeneous dictionary to store shared data for a BehaviourTree.
    ///</summary>
    public partial class Blackboard
    {
        /// <summary>
        /// Blackboard event type.
        /// </summary>
        public enum EventType
        {
            Add,
            Remove,
            Change
        }

        /// <summary>
        /// Blackboard key event.
        /// </summary>
        public struct KeyEvent
        {
            public KeyEvent(EventType type, string key, object value)
            {
                Type = type;
                Key = key;
                Value = value;
            }

            public EventType Type { get; }
            public string Key { get; }
            public object Value { get; }
        }

        [Newtonsoft.Json.JsonProperty] private readonly Dictionary<string, object> _memory = new Dictionary<string, object>();

        /// <summary>
        /// The internal memory of the blackboard.
        /// </summary>
        public IReadOnlyDictionary<string, object> Memory => this._memory;

        // Used to serailize the key names.
        // Note: Cannot be readonly since it will not serialize in the ScriptableObject.
#pragma warning disable IDE0044 // Add readonly modifier
        private List<string> _keys = new List<string>();
#pragma warning restore IDE0044 // Add readonly modifier

        private readonly List<Action<KeyEvent>> _observers = new List<Action<KeyEvent>>();

        public int AssetInstanceID;

        public string name;

        public int ObserverCount => this._observers.Count;
        
        /// <summary>
        /// The number of keys in the Blackboard.
        /// </summary>
        public int Count => this._memory.Count;
        
        ///<summary>
        /// Sets key in the blackboard with an unset value.
        ///</summary>
        public void Set(string key)
        {
            if (!this._memory.ContainsKey(key))
            {
                this._memory.Add(key, null);
                this.NotifyObservers(new KeyEvent(EventType.Add, key, null));
            }
        }

        /// <summary>
        /// Set the blackboard key to a value.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        public void Set(string key, object value)
        {
            if (!this._memory.ContainsKey(key))
            {
                this._memory.Add(key, value);
                this.NotifyObservers(new KeyEvent(EventType.Add, key, value));
            }
            else
            {
                var oldValue = this._memory[key];
                if ((oldValue == null && value != null) || (oldValue != null && !oldValue.Equals(value)))
                {
                    this._memory[key] = value;
                    this.NotifyObservers(new KeyEvent(EventType.Change, key, value));
                }
            }
        }

        ///<summary>
        /// Get the key value.
        ///</summary>
        /// <returns>
        /// <para>Key value of type T if it exists in the Blackboard and is set.</para>
        /// default(T) if it does not exist or is unset.
        /// </returns>
        public T Get<T>(string key)
        {
            object value = Get(key);
            if (value == null)
            {
                return default;
            }

            return (T)value;
        }

        /// <summary>
        /// Get the key value.
        /// </summary>
        /// <returns>Value at the key. Null if it is unset or if the Blackboard does not contain key.</returns>
        public object Get(string key)
        {
            if (Contains(key))
            {
                return this._memory[key];
            }

            return null;
        }

        ///<summary>
        /// Removes the key from the Blackboard.
        ///</summary>
        public void Remove(string key)
        {
            if (this._memory.Remove(key))
            {
                this.NotifyObservers(new KeyEvent(EventType.Remove, key, null));
            }
        }

        /// <summary>
        /// Sets the key value to null. Key must exist in the Blackboard.
        /// </summary>
        public void Unset(string key)
        {
            if (Contains(key))
            {
                this._memory[key] = null;
                this.NotifyObservers(new KeyEvent(EventType.Change, key, null));
            }
        }

        /// <summary>
        /// Removes all keys from the Blackboard.
        /// </summary>
        public void Clear()
        {
            this._memory.Clear();
        }

        /// <summary>
        /// Check if the key exists in the Blackboard.
        /// </summary>
        public bool Contains(string key)
        {
            return this._memory.ContainsKey(key);
        }

        /// <summary>
        /// Does the key exist and is the value not null?
        /// </summary>
        public bool IsSet(string key)
        {
            return Contains(key) && this._memory[key] != null;
        }

        /// <summary>
        /// Does the key exist is the value null?
        /// </summary>
        public bool IsUnset(string key)
        {
            return Contains(key) && this._memory[key] == null;
        }

        public void AddObserver(Action<KeyEvent> observer)
        {
            this._observers.Add(observer);
        }

        public void RemoveObserver(Action<KeyEvent> observer)
        {
            this._observers.Remove(observer);
        }
        
        /// <summary>
        /// Sets all Blackboard keys with unset values.
        /// </summary>
        [OnDeserialized]
        public void OnAfterDeserialize(StreamingContext context)
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
        [OnSerializing]
        public void OnBeforeSerialize(StreamingContext context)
        {
            this._keys.Clear();
            foreach (string key in this._memory.Keys)
            {
                this._keys.Add(key);
            }
        }

        private void NotifyObservers(KeyEvent e)
        {
            foreach (Action<KeyEvent> observer in this._observers)
            {
                observer(e);
            }
        }
    }
}