// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.ObjectPool
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections.Generic;

namespace BehaviorDesigner.Runtime
{
  public static class ObjectPool
  {
    private static Dictionary<Type, object> poolDictionary = new Dictionary<Type, object>();
    private static object lockObject = new object();

    public static T Get<T>()
    {
      lock (ObjectPool.lockObject)
      {
        if (ObjectPool.poolDictionary.ContainsKey(typeof (T)))
        {
          Stack<T> pool = ObjectPool.poolDictionary[typeof (T)] as Stack<T>;
          if (pool.Count > 0)
            return pool.Pop();
        }
        return (T) TaskUtility.CreateInstance(typeof (T));
      }
    }

    public static void Return<T>(T obj)
    {
      if ((object) obj == null)
        return;
      lock (ObjectPool.lockObject)
      {
        object obj1;
        if (ObjectPool.poolDictionary.TryGetValue(typeof (T), out obj1))
        {
          (obj1 as Stack<T>).Push(obj);
        }
        else
        {
          Stack<T> objStack = new Stack<T>();
          objStack.Push(obj);
          ObjectPool.poolDictionary.Add(typeof (T), (object) objStack);
        }
      }
    }

    public static void Clear()
    {
      lock (ObjectPool.lockObject)
        ObjectPool.poolDictionary.Clear();
    }
  }
}
