/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : GameObject 实例提供者。实现 ToolKit 抽象层 IInstanceProvider, 注入给 ResourceManager
 *                的实例对象池, 负责把预制体原型实例化/激活/失活/销毁。
 *                这样实例池逻辑(在 ResourceManager 中, 引擎无关)与 GameObject 操作(本类)解耦。
 *                注意: 需在 Unity 主线程使用。
 */

using ToolKit.Tools.Common;
using UnityEngine;

namespace UnityToolKit.Engine.ResourceSystem
{
    public sealed class GameObjectInstanceProvider : IInstanceProvider
    {
        private readonly Transform _poolRoot;

        /// <param name="poolRoot">失活实例的挂载根节点, 不传则自动创建一个 DontDestroyOnLoad 的隐藏根</param>
        public GameObjectInstanceProvider(Transform poolRoot = null)
        {
            if (poolRoot == null)
            {
                var go = new GameObject("[ResInstancePoolRoot]");
                Object.DontDestroyOnLoad(go);
                go.SetActive(false);
                poolRoot = go.transform;
            }
            _poolRoot = poolRoot;
        }

        // 只有 GameObject 原型可被实例化/池化; Sprite/Material 等可应用的共享资源会在此被拒绝。
        public bool CanInstantiate(IAssetHandle prototype)
        {
            return prototype != null && prototype.GetAsset<GameObject>() != null;
        }

        public object Create(IAssetHandle prototype)
        {
            var prefab = prototype.GetAsset<GameObject>();
            if (prefab == null)
            {
                Log.Error($"[ResourceSystem] 原型不是 GameObject, 无法实例化: {prototype.Address}");
                return null;
            }
            return Object.Instantiate(prefab);
        }

        public void OnGet(object instance)
        {
            if (instance is GameObject go)
            {
                go.SetActive(true);
            }
        }

        public void OnReturn(object instance)
        {
            if (instance is GameObject go)
            {
                go.SetActive(false);
                go.transform.SetParent(_poolRoot, false);
            }
        }

        public void OnDestroy(object instance)
        {
            if (instance is GameObject go)
            {
                Object.Destroy(go);
            }
        }

        // 利用 Unity 对 == 的重载: 已被 Destroy 的 GameObject 在此返回 false。
        public bool IsAlive(object instance)
        {
            return instance is GameObject go && go != null;
        }
    }
}
