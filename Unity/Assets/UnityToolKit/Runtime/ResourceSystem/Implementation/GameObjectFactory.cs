using System;
using ToolKit.Tools.Common;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityToolKit.Runtime.ResourceSystem
{
    /// <summary>
    /// GameObject 实例工厂：原型是 Prefab，实例是 Object.Instantiate 出来的对象。
    /// 交付时激活，归还时停用；销毁用 Object.Destroy (引擎在帧末完成)。
    /// </summary>
    public sealed class GameObjectFactory : IInstanceFactory
    {
        private readonly Transform _idleParent;

        /// <param name="idleParent">闲置实例挂到的节点；null 表示留在原处</param>
        public GameObjectFactory(Transform idleParent = null)
        {
            _idleParent = idleParent;
        }

        public Type PrototypeType => typeof(GameObject);

        public object Create(object prototype)
        {
            if (!(prototype is GameObject prefab)) throw new ArgumentException("原型不是 GameObject", nameof(prototype));
            return Object.Instantiate(prefab);
        }

        public void OnRent(object instance)
        {
            var gameObject = Alive(instance);
            gameObject.SetActive(true);
        }

        public void OnReturn(object instance)
        {
            var gameObject = Alive(instance);
            gameObject.SetActive(false);
            if (_idleParent != null) gameObject.transform.SetParent(_idleParent, false);
        }

        public void Destroy(object instance)
        {
            if (instance is Object unityObject && unityObject != null) Object.Destroy(unityObject);
        }

        private static GameObject Alive(object instance)
        {
            if (instance is GameObject gameObject && gameObject != null) return gameObject;
            throw new InvalidOperationException("实例已被引擎销毁或不是 GameObject");
        }
    }
}