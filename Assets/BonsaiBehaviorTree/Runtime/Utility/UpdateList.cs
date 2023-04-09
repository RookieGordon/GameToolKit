using System;
using System.Collections.Generic;

namespace Bonsai.Utility
{
    /// <summary>
    /// Defers list modification so it is safe to traverse during an update call.
    /// </summary>
    public class UpdateList<T>
    {
        private readonly List<T> _data = new List<T>();
        private readonly List<T> _addQueue = new List<T>();
        private readonly List<T> _removeQueue = new List<T>();
        private readonly Predicate<T> _isInRemovalQueue;

        public IReadOnlyList<T> Data => this._data;

        public UpdateList()
        {
            this._isInRemovalQueue = value => this._removeQueue.Contains(value);
        }

        /// <summary>
        /// Queues an item to add to the list.
        /// </summary>
        /// <param name="item">The item to add.</param>
        public void Add(T item)
        {
            this._addQueue.Add(item);
        }

        /// <summary>
        /// Queues an item for removal from the list.
        /// </summary>
        /// <param name="item">The item to remove.</param>
        public void Remove(T item)
        {
            this._removeQueue.Add(item);
        }

        /// <summary>
        /// Removes and adds pending items in the modification queues.
        /// </summary>
        public void AddAndRemoveQueued()
        {
            if (this._removeQueue.Count != 0)
            {
                this._data.RemoveAll(this._isInRemovalQueue);
                this._removeQueue.Clear();
            }

            if (this._addQueue.Count != 0)
            {
                this._data.AddRange(this._addQueue);
                this._addQueue.Clear();
            }
        }
    }
}