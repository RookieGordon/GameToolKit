/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 业务资源持有凭证 (P1, §6.5)。非池化 sealed class，一次持有，释放后永久失效。
 *                Dispose 幂等且允许任意线程 (一次性门闩 + 投递归还)；Value/IsValid/Retain
 *                为同步操作，要求在上下文调用。凭证的一次性门闩即防重复归还机制，
 *                归还身份由内部 entry 引用保证。
 */

using System;
using System.Threading;

namespace ToolKit.Tools.Common
{
    public sealed class ResourceRef<T> : IDisposable where T : class
    {
        private readonly LoadManager _loads;
        private ResourceEntry _entry;
        private readonly long _leaseId;
        private int _disposed;

        internal ResourceRef(LoadManager loads, ResourceEntry entry, long leaseId)
        {
            _loads = loads;
            _entry = entry;
            _leaseId = leaseId;
        }

        /// <summary> 底层资源对象。已释放抛 ObjectDisposedException；底层失效抛 ResourceLoadException </summary>
        public T Value
        {
            get
            {
                _loads.Context.AssertAccess();
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(ResourceRef<T>),
                        $"资源引用已释放，不能再访问 Value (lease={_leaseId}, key={_entry.Key})");
                }
                return (T)_loads.GetLiveAsset(_entry, _leaseId);
            }
        }

        /// <summary> 轻量存活查询，不延长持有 </summary>
        public bool IsValid
        {
            get
            {
                _loads.Context.AssertAccess();
                return Volatile.Read(ref _disposed) == 0 && _loads.IsEntryLive(_entry);
            }
        }

        /// <summary> 新的一份独立持有；系统 Closing 或本引用已释放/底层失效时禁止 </summary>
        public ResourceRef<T> Retain()
        {
            _loads.Context.AssertAccess();
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(ResourceRef<T>), "资源引用已释放，不能 Retain");
            }
            return _loads.Retain<T>(_entry);
        }

        /// <summary> 幂等；投递一次归还操作，对象对外立即失效 </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            var entry = _entry;
            _loads.Context.Post(() => _loads.Release(entry, _leaseId));
        }

        internal long LeaseId => _leaseId;

        public override string ToString()
        {
            return $"ResourceRef<{typeof(T).Name}>(lease={_leaseId}, key={_entry.Key}, disposed={Volatile.Read(ref _disposed) != 0})";
        }
    }
}
