using System;
using System.Collections.Generic;

namespace ToolKit.Tools.Extension
{
    public class ListDictionary<TKey, TValue> : Dictionary<TKey, List<TValue>> where TKey : notnull
    {
        private int _listCapacity = 0;
        private bool _checkDuplicate = false;

        public ListDictionary() : base()
        {
            
        }

        public ListDictionary(int capacity) : base(capacity)
        {
        }

        public ListDictionary(int capacity, int listCapacity) : base(capacity)
        {
        }

        public ListDictionary(int capacity, int listCapacity, bool checkDuplicate) : base(capacity)
        {
            _listCapacity = listCapacity;
            _checkDuplicate = checkDuplicate;
        }

        public void Add(TKey key, TValue value)
        {
            if (!TryGetValue(key, out var l))
            {
                l = _listCapacity > 0 ? new List<TValue>(_listCapacity): new List<TValue>();
                base.Add(key, l);
            }

            if (_checkDuplicate && l.Contains(value))
            {
                throw new ArgumentException("An element with the same value already exists in the List<TValue>.");
            }

            l.Add(value);
        }

        public bool Remove(TKey key, TValue value)
        {
            if (!TryGetValue(key, out var l) || !l.Remove(value))
            {
                return false;
            }

            if (l.Count == 0)
            {
                base.Remove(key);
            }

            return true;
        }

        public new void Clear()
        {
            foreach (var l in Values)
            {
                l.Clear();
            }

            base.Clear();
        }
    }
}
