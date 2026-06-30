using System;
using UnityEngine;

namespace UnityToolKit.Runtime.Common
{
    public class UnitySingleton<T>: MonoBehaviour where T : Component
    {
        private static GameObject _singletonRoot;
        
        private static T _instance;

        public static T Instance
        {
            get
            {
                if (_singletonRoot == null)
                {
                    _singletonRoot = new GameObject("SingletonRoot");
                    DontDestroyOnLoad(_singletonRoot);
                }

                if (_instance == null)
                {
                    _instance = _singletonRoot.transform.GetComponentInChildren<T>();
                    if (_instance == null)
                    {
                        var obj = new GameObject($"{typeof(T).Name}");
                        DontDestroyOnLoad(obj);
                        obj.transform.SetParent(_singletonRoot.transform);
                        obj.hideFlags = HideFlags.HideAndDontSave;
                        _instance = obj.AddComponent<T>();
                    }
                }

                return _instance;
            }
        }
    }
}