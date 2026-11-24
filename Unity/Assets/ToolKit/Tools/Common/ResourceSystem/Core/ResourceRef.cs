/*
 * description  : 资源条目、卸载结果与业务持有的独立引用凭证。
 *                条目状态互斥由 ResourceStore 的表结构直接表达，不设状态标志位；
 *                引用归还幂等，归还后本凭证立即失效，不因同对象另有持有者而恢复。
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 已就绪的产物条目：具体对象 + 唯一一份释放责任 + 当前持有计数。
    /// 计数由 ResourceStore 的监视器锁保护。
    /// </summary>
    public sealed class ResourceEntry
    {
        internal ResourceEntry(ResourceKey key, IResource product, int holds)
        {
            Key = key;
            Product = product;
            Holds = holds;
        }

        public ResourceKey Key { get; }
        internal IResource Product { get; }
        public object Value => Product.Value;

        /// <summary> 当前业务持有份数 </summary>
        public int Holds { get; internal set; }
    }

    /// <summary> 释放失败后保留的残留：对象仍在，原因保留，下次显式卸载时再试 </summary>
    public sealed class ResourceResidue
    {
        internal ResourceResidue(ResourceEntry entry, Exception error)
        {
            Entry = entry;
            Error = error;
        }

        public ResourceEntry Entry { get; }
        public Exception Error { get; }
        public ResourceKey Key => Entry.Key;
    }

    /// <summary> 一次卸载的结果；尝试数不等于成功数 </summary>
    public sealed class UnloadResult
    {
        public List<ResourceKey> Released { get; } = new List<ResourceKey>();
        public List<ResourceKey> SkippedInUse { get; } = new List<ResourceKey>();
        public List<ResourceResidue> Failed { get; } = new List<ResourceResidue>();
    }

    /// <summary> 业务持有的资源引用凭证：表达一份独立持有，归还即失效 </summary>
    public sealed class ResourceRef<T> : IDisposable
    {
        private readonly ResourceStore _store;
        private readonly ResourceEntry _entry;
        private int _disposed;

        internal ResourceRef(ResourceStore store, ResourceEntry entry)
        {
            _store = store;
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
            if (Volatile.Read(ref _disposed) != 0 || !_store.TryRetain(_entry))
                throw ResourceException.Of(DiagnosticCodes.AssetInvalidated, LoadStage.LoadAsset, _entry.Key);
            if (Volatile.Read(ref _disposed) != 0)
            {
                _store.Release(_entry);
                throw ResourceException.Of(DiagnosticCodes.AssetInvalidated, LoadStage.LoadAsset, _entry.Key);
            }
            return new ResourceRef<T>(_store, _entry);
        }

        /// <summary> 归还；幂等，任意线程，不阻塞 </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _store.Release(_entry);
        }
    }
}
