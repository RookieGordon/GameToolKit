using System;
using System.Collections.Generic;

namespace ToolKit.Tools.Common
{
    public class ListDictionary<TKey, TValue> : Dictionary<TKey, List<TValue>>
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
            _listCapacity = listCapacity;
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
                l = new List<TValue>(_listCapacity);
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
            if (TryGetValue(key, out var l))
            {
                l.Remove(value);
                if (l.Count == 0)
                {
                    base.Remove(key);
                }

                return true;
            }

            return false;
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