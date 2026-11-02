/*
 * author       : Gordon
 * datetime     : 2026-10-15 (2026-11-02 按新契约重写)
 * description  : Unity 适配层：执行上下文绑定主线程 (借用 Unity 的 SynchronizationContext，不需要每帧 Tick)，
 *                GameObject 实例工厂用 Object.Instantiate/SetActive/Destroy，图片转换用 ImageConversion。
 *                这里只把既定契约适配为引擎调用，不新增资源编排。放在 Unity 程序集内，不参与控制台构建。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityToolKit.Runtime.Resource
{
    /// <summary>
    /// 绑定主线程的执行上下文。必须在主线程上创建；跨线程调用通过主线程的 SynchronizationContext 投递，
    /// 由引擎在下一帧排空，调用线程不阻塞。
    /// </summary>
    public sealed class UnityExecutionContext : IExecutionContext
    {
        private readonly SynchronizationContext _main;
        private readonly int _mainThreadId;

        public UnityExecutionContext()
        {
            _main = SynchronizationContext.Current
                    ?? throw new InvalidOperationException("UnityExecutionContext 必须在 Unity 主线程上创建");
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        public void Post(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (IsMainThread) { Guarded(action); return; }
            _main.Post(_ => Guarded(action), null);
        }

        public Task RunAsync(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return RunAsync(() => { action(); return true; });
        }

        public Task<T> RunAsync<T>(Func<T> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (IsMainThread) Complete(completion, function);
            else _main.Post(_ => Complete(completion, function), null);
            return completion.Task;
        }

        public async Task<T> InvokeAsync<T>(Func<Task<T>> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            var started = await RunAsync(function).ConfigureAwait(false);   // 同步部分在主线程启动
            return await started.ConfigureAwait(false);
        }

        private static void Complete<T>(TaskCompletionSource<T> completion, Func<T> function)
        {
            try { completion.TrySetResult(function()); }
            catch (OperationCanceledException error)
            {
                completion.TrySetCanceled(error.CancellationToken.IsCancellationRequested
                    ? error.CancellationToken : new CancellationToken(canceled: true));
            }
            catch (Exception error) { completion.TrySetException(error); }
        }

        private static void Guarded(Action action)
        {
            try { action(); }
            catch (Exception error) { Debug.LogError("[ResourceSystem] 投递的操作抛出异常：" + error); }
        }
    }

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

    /// <summary> 内置转换：byte[] (PNG/JPG) → Texture2D；产物释放时 Destroy 纹理。注册到目标类型 Texture2D。 </summary>
    public sealed class TextureConverter : IProductConverter
    {
        private readonly bool _readable;

        /// <param name="readable">是否保留 CPU 侧可读副本；false 更省内存</param>
        public TextureConverter(bool readable = false)
        {
            _readable = readable;
        }

        public Task<IResource> ConvertAsync(IResource source, ResourceKey key, CancellationToken cancellation)
        {
            if (!(source.Value is byte[] bytes))
                throw ResourceException.Of(DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, key);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, bytes, !_readable))
            {
                Object.Destroy(texture);
                throw ResourceException.Of(DiagnosticCodes.AssetConversionFailed, LoadStage.Convert, key);
            }
            return Task.FromResult<IResource>(new Product(texture, () => { if (texture != null) Object.Destroy(texture); }));
        }
    }
}
