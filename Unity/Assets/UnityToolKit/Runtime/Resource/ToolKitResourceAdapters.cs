/*
 * author       : Gordon
 * datetime     : 2026-10-15
 * description  : Unity 适配层：状态串行域绑定主线程，实例工厂用 Object.Instantiate/Destroy，
 *                引擎存活与销毁按引擎语义判断。这里只把既定契约适配为引擎调用，不新增资源编排。
 *                放在 Unity 程序集内 (依赖 UnityEngine)，因此不参与控制台构建。
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;
using Object = UnityEngine.Object;
// UnityEngine 也有 ResourceRequest：这里明确取资源系统的那一个，避免与引擎类型歧义。
using ResourceRequest = ToolKit.Tools.Common.ResourceRequest;

namespace UnityToolKit.Runtime.Resource
{
    /// <summary>
    /// 绑定主线程的状态串行域。业务帧调用 <see cref="Tick"/> 排空投递队列；
    /// 队列是唯一的跨线程入口，Post/RunAsync 都不阻塞调用线程。
    /// </summary>
    public sealed class UnityExecutionContext : IExecutionContext
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private readonly int _mainThreadId;
        private int _draining;

        public UnityExecutionContext()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public bool IsCurrent => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        public void AssertAccess()
        {
            if (IsCurrent) return;
            throw new InvalidOperationException(
                "资源状态只能在主线程访问；跨线程请使用可等待调度或归还投递");
        }

        /// <summary> 投递一次状态变更；任意线程可用，调用者不等待 </summary>
        public void Post(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (IsCurrent)
            {
                // 同上下文投递等于把状态修改嵌进调用栈：直接执行，仍在本线程内保持串行。
                action();
                return;
            }
            _queue.Enqueue(action);
        }

        /// <summary> 主线程内同步执行；跨线程调用是误用 (不跨线程等待，避免死锁) </summary>
        public void Invoke(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            AssertAccess();
            action();
        }

        public T Invoke<T>(Func<T> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            AssertAccess();
            return function();
        }

        public Task RunAsync(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return RunAsync(() => { action(); return true; });
        }

        public Task<T> RunAsync<T>(Func<T> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            if (IsCurrent)
            {
                try
                {
                    return Task.FromResult(function());
                }
                catch (OperationCanceledException error)
                {
                    return Task.FromCanceled<T>(error.CancellationToken.IsCancellationRequested
                        ? error.CancellationToken : new CancellationToken(canceled: true));
                }
                catch (Exception error)
                {
                    return Task.FromException<T>(error);
                }
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(() =>
            {
                try
                {
                    completion.TrySetResult(function());
                }
                catch (OperationCanceledException error)
                {
                    completion.TrySetCanceled(error.CancellationToken.IsCancellationRequested
                        ? error.CancellationToken : new CancellationToken(canceled: true));
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            });
            return completion.Task;
        }

        public async Task<T> InvokeAsync<T>(Func<Task<T>> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            Task<T> started;
            if (IsCurrent)
            {
                started = function();
            }
            else
            {
                var slot = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _queue.Enqueue(() =>
                {
                    try { slot.TrySetResult(function()); }
                    catch (Exception error) { slot.TrySetException(error); }
                });
                started = await slot.Task.ConfigureAwait(false);
            }
            return await started.ConfigureAwait(false);
        }

        /// <summary> 引擎帧调用：主线程排空投递队列 </summary>
        public void Tick()
        {
            if (!IsCurrent)
            {
                throw new InvalidOperationException("Tick 必须在主线程调用");
            }
            if (Interlocked.Exchange(ref _draining, 1) != 0) return;
            try
            {
                while (_queue.TryDequeue(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception error)
                    {
                        Debug.LogError($"[ResourceSystem] 状态提交抛出异常：{error}");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _draining, 0);
            }
        }
    }

    /// <summary>
    /// GameObject 实例工厂：原型是 Prefab，实例是 Object.Instantiate 出来的对象。
    /// 销毁按引擎语义等待真实结束 (Destroy 延后完成)。
    /// </summary>
    public sealed class UnityGameObjectFactory : IInstanceFactory
    {
        private readonly string _instanceKey;

        public UnityGameObjectFactory(string instanceKey = "default")
        {
            _instanceKey = string.IsNullOrEmpty(instanceKey) ? "default" : instanceKey;
        }

        public ResourceRequest GetPrototypeRequest(ResourceRequest instanceRequest)
        {
            // 原型种类由实例请求决定：默认取 GameObject；调用方可通过 Parameters 提供自定义种类。
            return new ResourceRequest(instanceRequest.Source, instanceRequest.Address,
                typeof(GameObject), instanceRequest.Parameters);
        }

        public string GetInstanceKey(ResourceRequest instanceRequest) => _instanceKey;

        public bool CanCreate(object prototype, Type instanceType)
        {
            if (prototype is not GameObject) return false;
            return instanceType == typeof(GameObject) || instanceType.IsAssignableFrom(typeof(GameObject));
        }

        public Task<object> CreateAsync(object prototype, ResourceRequest creationRequest, CancellationToken operationToken)
        {
            operationToken.ThrowIfCancellationRequested();
            if (prototype is not GameObject prefab)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.InstanceCreateFailed, LoadStage.Instantiate, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "reason", "原型不是 GameObject" } }));
            }
            var instance = Object.Instantiate(prefab);
            return Task.FromResult<object>(instance);
        }

        public bool IsAlive(object instance) => instance is Object unityObject && unityObject != null;

        public void OnRent(object instance)
        {
            if (instance is GameObject gameObject) gameObject.SetActive(true);
        }

        public void OnReturn(object instance)
        {
            if (instance is GameObject gameObject) gameObject.SetActive(false);
        }

        /// <summary> Destroy 延后到帧末完成：这里等到对象真的为 null 才算确认结束 </summary>
        public async Task DestroyAsync(object instance)
        {
            if (instance is not Object unityObject) return;
            if (unityObject == null) return;
            Object.Destroy(unityObject);
            while (unityObject != null)
            {
                await Task.Yield();
            }
        }
    }
}
