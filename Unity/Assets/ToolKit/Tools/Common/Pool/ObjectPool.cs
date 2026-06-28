/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : 通用对象池 (引擎无关)。与 SimplePool 互补:
 *                  - SimplePool<T>  : 要求 T 实现 ISetupable/IClearable/IDisposable, 池空时用无参构造自动创建;
 *                  - ObjectPool<T>  : 用外部注入的工厂创建对象, 取/还/销毁均走委托回调,
 *                                     因此可以池化 GameObject、第三方实例等"无法无参构造"的对象。
 *                资源系统的实例对象池即基于本类实现 (工厂 = 由资源原型实例化)。
 */

using System;
using System.Collections.Generic;

namespace ToolKit.Tools.Common
{
    public sealed class ObjectPool<T> : IDisposable where T : class
    {
        private readonly Func<T> _factory;
        private readonly Action<T> _onGet;
        private readonly Action<T> _onReturn;
        private readonly Action<T> _onDestroy;
        private readonly Queue<T> _queue = new Queue<T>();

        public int Capacity { get; }
        public int Count => _queue.Count;

        /// <param name="factory">创建新对象的工厂 (池空时调用), 必填</param>
        /// <param name="onGet">取出对象时回调 (如激活), 可空</param>
        /// <param name="onReturn">归还对象时回调 (如失活), 可空</param>
        /// <param name="onDestroy">超出容量或清理时销毁对象的回调, 可空</param>
        /// <param name="capacity">池容量上限, &lt;=0 表示不限制</param>
        public ObjectPool(Func<T> factory, Action<T> onGet = null, Action<T> onReturn = null, Action<T> onDestroy = null, int capacity = 100)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _onGet = onGet;
            _onReturn = onReturn;
            _onDestroy = onDestroy;
            Capacity = capacity;
        }

        /// <summary> 取出一个对象 (池空则用工厂创建) </summary>
        public T Get()
        {
            var obj = _queue.Count > 0 ? _queue.Dequeue() : _factory();
            _onGet?.Invoke(obj);
            return obj;
        }

        /// <summary> 归还一个对象。超出容量则直接销毁 </summary>
        public void Return(T obj)
        {
            if (obj == null)
            {
                return;
            }

            _onReturn?.Invoke(obj);

            if (Capacity > 0 && _queue.Count >= Capacity)
            {
                _onDestroy?.Invoke(obj);
                return;
            }
            _queue.Enqueue(obj);
        }

        /// <summary> 预热: 预先创建 count 个对象放入池中 </summary>
        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var obj = _factory();
                _onReturn?.Invoke(obj);
                _queue.Enqueue(obj);
            }
        }

        /// <summary> 销毁池中所有缓存对象 </summary>
        public void Clear()
        {
            while (_queue.Count > 0)
            {
                _onDestroy?.Invoke(_queue.Dequeue());
            }
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
