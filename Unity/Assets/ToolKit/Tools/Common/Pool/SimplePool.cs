/*
 * author       : Gordon
 * datetime     : 2025/1/18
 * description  : 简易对象池。对象必须实现ISetupable, IClearable, IDisposable接口
 *                1、 Push方法将对象放回池中，对象会调用Clear方法
 *                2、 Pop方法从池中取出对象，对象会调用Setup方法
 *                3、 Dispose方法释放池中所有对象，对象会调用Dispose方法
 *                4、 如果设置了池子的容量，那么当池子中对象数量超过容量时，Push的对象会被Dispose
 */

using System;
using System.Collections.Generic;
using ToolKit.Common;

namespace ToolKit.Tools.Common
{
    public class SimplePool<T> : IDisposable where T : class, ISetupable, IClearable, IDisposable
    {
        public int Count => _queue.Count;
        public int Capacity { get; private set; }

        private Queue<T> _queue;

        public SimplePool(int capacity = 1000)
        {
            Capacity = capacity;
            _queue = new Queue<T>();
        }

        public void Push(T obj)
        {
            if (obj is IClearable clearable)
            {
                clearable.Clear();
            }
            
            _queue.Enqueue(obj);

            if (Capacity > 0 && Count >= Capacity)
            {
                var removeCount = Count / 2;
                while (removeCount-- > 0)
                {
                    var excessObj = _queue.Dequeue();
                    excessObj.Dispose();
                }
            }
        }

        public T Pop()
        {
            T obj = this._queue.Count > 0 ? this._queue.Dequeue() : System.Activator.CreateInstance<T>();
            if (obj is ISetupable setupable)
            {
                setupable.Setup();
            }

            return obj;
        }

        public void Dispose()
        {
            while (_queue.Count > 0)
            {
                T obj = _queue.Dequeue();
                obj.Dispose();
            }

            _queue = null;
        }
    }
}