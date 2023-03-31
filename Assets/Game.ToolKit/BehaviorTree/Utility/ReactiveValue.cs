using System;

namespace Bonsai.Utility
{
    /// <summary>
    /// Raises an event whenever the value is set.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class ReactiveValue<T>
    {
        private T _value;

        public event EventHandler<T> ValueChanged;

        public T Value
        {
            get => this._value;
            set
            {
                this._value = value;
                OnValueChanged();
            }
        }

        public ReactiveValue()
        {
        }

        public ReactiveValue(T value)
        {
            this._value = value;
        }

        protected virtual void OnValueChanged()
        {
            ValueChanged?.Invoke(this, this._value);
        }
    }
}