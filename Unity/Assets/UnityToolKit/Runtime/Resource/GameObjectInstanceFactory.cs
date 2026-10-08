/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : GameObject 实例工厂 (P5, §3.5)。适配创建/租用重置/归还重置/销毁确认；
 *                Unity Destroy 延后完成 —— DestroyAsync 轮询到实例确认销毁 (== null) 才返回，
 *                原型引用至少保留到确认 (池的 Destroying 计数保护)。
 *                byte[]、Sprite 等共享对象不进入实例池 (CanCreate 只接受 GameObject 原型)。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common.Resource;
using UnityEngine;
using Object = UnityEngine.Object;
using ResourceRequest = ToolKit.Tools.Common.Resource.ResourceRequest;

namespace UnityToolKit.Runtime.Resource
{
    public sealed class GameObjectInstanceFactory : IInstanceFactory
    {
        private readonly IExecutionContext _context;
        private readonly Transform? _poolRoot;

        /// <param name="poolRoot">失活实例挂载根；不传则创建 DontDestroyOnLoad 的隐藏根</param>
        public GameObjectInstanceFactory(IExecutionContext context, Transform? poolRoot = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            if (poolRoot == null)
            {
                var host = new GameObject("[ResourceInstancePoolRoot]");
                Object.DontDestroyOnLoad(host);
                host.SetActive(false);
                poolRoot = host.transform;
            }
            _poolRoot = poolRoot;
        }

        public ResourceRequest GetPrototypeRequest(ResourceRequest instanceRequest)
        {
            // GameObject 实例的原型即同地址的 GameObject 资源
            return new ResourceRequest(instanceRequest.LoaderId, instanceRequest.Address,
                typeof(GameObject), instanceRequest.Parameters);
        }

        public string GetInstanceKey(ResourceRequest instanceRequest)
        {
            return "gameObject"; // 普通工厂固定值：实例表示由结果类型决定
        }

        public bool CanCreate(object prototype, Type instanceType)
        {
            // 只有 GameObject 原型可实例化；Sprite/Material/byte[] 等共享资源被挡在实例池之外
            return prototype is GameObject && instanceType == typeof(GameObject);
        }

        public Task<object> CreateAsync(object prototype, ResourceRequest creationRequest, CancellationToken operationToken)
        {
            return _context.InvokeAsync(() =>
            {
                var prefab = (GameObject)prototype;
                if (prefab == null)
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate,
                        CleanupStatus.Complete, null,
                        new Dictionary<string, object> { { "reason", "prototype-destroyed" } }));
                }
                return Task.FromResult<object>(Object.Instantiate(prefab));
            });
        }

        public bool IsAlive(object instance)
        {
            // 利用 Unity == 重载：已 Destroy 的 GameObject 判 false
            return instance is GameObject go && go != null;
        }

        public void OnRent(object instance)
        {
            if (instance is GameObject go && go != null)
            {
                go.SetActive(true);
            }
        }

        public void OnReturn(object instance)
        {
            if (instance is GameObject go && go != null)
            {
                go.SetActive(false);
                if (_poolRoot != null)
                {
                    go.transform.SetParent(_poolRoot, false);
                }
            }
        }

        public async Task DestroyAsync(object instance)
        {
            if (instance is not GameObject go || go == null)
            {
                return; // 已被外部销毁："已不存在"视为成功
            }
            _context.Invoke(() =>
            {
                if (go != null)
                {
                    Object.Destroy(go); // 延后到帧末生效
                }
            });

            // Unity Destroy 延后完成：确认实例销毁后才允许释放原型引用
            var deadline = Environment.TickCount + 10_000;
            while (Environment.TickCount < deadline)
            {
                var destroyed = _context.Invoke(() => go == null);
                if (destroyed)
                {
                    return;
                }
                await Task.Delay(15).ConfigureAwait(false);
            }
        }
    }
}
