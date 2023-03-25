namespace Bonsai.Utility
{
    /// <summary>
    /// A fixed-sized array that provides stack operations.
    /// It also provides random-access to contents.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class FixedSizeStack<T>
    {
        private readonly T[] _container;

        public FixedSizeStack(int length)
        {
            Count = 0;
            this._container = new T[length];
        }

        /// <summary>
        /// Reset count and set all entries to the default T value.
        /// </summary>
        public void Clear()
        {
            Count = 0;
            for (int i = 0; i < this._container.Length; ++i)
            {
                this._container[i] = default;
            }
        }

        /// <summary>
        /// Reset the count. Entries are left as is.
        /// </summary>
        public void ResetCount()
        {
            Count = 0;
        }

        public T Peek()
        {
            return this._container[Count - 1];
        }

        public T Pop()
        {
            return this._container[--Count];
        }

        public void Push(T value)
        {
            this._container[Count++] = value;
        }

        public int Count { get; private set; }

        public T GetValue(int index)
        {
            return this._container[index];
        }
    }
}