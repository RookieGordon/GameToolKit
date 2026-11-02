/*
 * description  : 业务持有的资源引用凭证：一个业务的一份持有，归还即失效。
 *                只持有和归还，不调度加载或卸载；持有计数的唯一事实在 ResourceEntry 里。
 */

using System;
using System.Threading;

namespace ToolKit.Tools.Common
{
    public sealed class ResourceRef<T> : IDisposable
    {
        private readonly ResourceCache _cache;
        private readonly ResourceEntry _entry;
        private int _disposed;

        internal ResourceRef(ResourceCache cache, ResourceEntry entry)
        {
            _cache = cache;
            _entry = entry;
        }

        /// <summary> 资源对象；本凭证已归还时抛出，即使同对象另有持有者也不恢复 </summary>
        public T Value
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw ResourceException.Of(DiagnosticCodes.AssetInvalidated, LoadStage.LoadAsset, _entry.Key);
                return (T)_entry.Value;
            }
        }

        public ResourceKey Key => _entry.Key;

        /// <summary> 本凭证是否仍然有效；纯查询，任意线程 </summary>
        public bool IsValid => Volatile.Read(ref _disposed) == 0;

        /// <summary> 追加一份独立持有：新凭证与本凭证各自归还、互不影响 </summary>
        public ResourceRef<T> Retain()
        {
            if (Volatile.Read(ref _disposed) != 0 || !_cache.TryRetain(_entry))
                throw ResourceException.Of(DiagnosticCodes.AssetInvalidated, LoadStage.LoadAsset, _entry.Key);
            return new ResourceRef<T>(_cache, _entry);
        }

        /// <summary> 归还；幂等，任意线程，不阻塞 </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cache.Release(_entry);
        }
    }
}
