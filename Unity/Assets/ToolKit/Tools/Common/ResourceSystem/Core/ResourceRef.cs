/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 业务资源持有凭证 (P1, §6.5)。非池化 sealed class，一次持有，释放后永久失效。
 *                Dispose 幂等且允许任意线程 (上下文内立即归还，其他线程投递)；Value/IsValid/Retain
 *                为同步操作，要求在上下文调用。凭证的一次性门闩即防重复归还机制，
 *                归还身份由内部 entry 引用保证。
 */

using System;
using System.Threading;

namespace ToolKit.Tools.Common
{
    public sealed class ResourceRef<T> : IDisposable where T : class
    {
        private readonly ResourceStore _store;
        private readonly ResourceEntry _entry;
        private readonly long _leaseId;
        private int _disposed;

        internal ResourceRef(ResourceStore store, ResourceEntry entry, long leaseId)
        {
            _store = store;
            _entry = entry;
            _leaseId = leaseId;
        }

        /// <summary> 底层资源对象。已释放抛 ObjectDisposedException；底层失效抛 ResourceLoadException </summary>
        public T Value
        {
            get
            {
                _store.Context.AssertAccess();
                return _store.Context.Invoke(() =>
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        throw new ObjectDisposedException(nameof(ResourceRef<T>),
                            $"资源引用已释放，不能再访问 Value (lease={_leaseId}, key={_entry.Key})");
                    }
                    return (T)_store.GetLiveAsset(_entry, _leaseId);
                });
            }
        }

        /// <summary> 轻量存活查询，不延长持有 </summary>
        public bool IsValid
        {
            get
            {
                _store.Context.AssertAccess();
                return _store.Context.Invoke(() =>
                    Volatile.Read(ref _disposed) == 0 && _store.IsReferenceValid(_entry));
            }
        }

        /// <summary> 新的一份独立持有；管理器已 Dispose 或本引用已释放/底层失效时禁止 </summary>
        public ResourceRef<T> Retain()
        {
            _store.Context.AssertAccess();
            return _store.Context.Invoke(() =>
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(ResourceRef<T>), "资源引用已释放，不能 Retain");
                }
                return _store.Retain<T>(_entry, _leaseId);
            });
        }

        /// <summary> 幂等；当前上下文立即归还，其他线程投递归还，对外立即失效 </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            var entry = _entry;
            var context = _store.Context;
            if (context.IsCurrent) context.Invoke(() => _store.ReleaseReference(entry, _leaseId));
            else context.Post(() => _store.ReleaseReference(entry, _leaseId));
        }

        // 调用者已经持有本凭证，并在执行上下文中将原型所有权交给脏实例。
        // 此处不批准 Value 访问；失效原型也可能仍拥有需要保留的底层依赖。
        internal ResourceRef<T> RetainForCleanup() => _store.AddReference<T>(_entry);

        public override string ToString()
        {
            return $"ResourceRef<{typeof(T).Name}>(lease={_leaseId}, key={_entry.Key}, disposed={Volatile.Read(ref _disposed) != 0})";
        }
    }
}
