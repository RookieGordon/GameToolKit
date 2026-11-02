/*
 * description  : 产物缓存：复用索引 + 所有权集合 + 持有计数 + 卸载。
 *                一把锁串行全部状态变更；释放动作在执行上下文内执行 (引擎调用)。
 *                这里只保存事实和执行卸载，不调度加载：加载由 LoadManager 推进，命中与登记都从这里走。
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 产物记录：对象 + 唯一一份释放责任 + 持有计数。
    /// 从复用索引撤下 (同键新产物登记、或卸载失败) 不等于已释放：仍在所有权集合里，业务仍可选择卸载。
    /// </summary>
    public sealed class ResourceEntry
    {
        internal ResourceEntry(ResourceKey key, IResource product)
        {
            Key = key;
            Product = product;
        }

        public ResourceKey Key { get; }
        internal IResource Product { get; }
        public object Value => Product.Value;

        /// <summary> 业务持有份数；由 ResourceCache 的锁保护 </summary>
        public int Holds { get; internal set; }

        /// <summary> 仍在复用索引中 (新的同键请求会命中它) </summary>
        public bool Indexed { get; internal set; } = true;

        /// <summary> 释放责任已被取走：之后不能再追加持有 </summary>
        public bool Unloading { get; internal set; }
    }

    /// <summary> 卸载失败后保留的残留：对象仍在，原因保留，下次卸载再试 </summary>
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

    public sealed class ResourceCache
    {
        private readonly object _gate = new object();
        private readonly Dictionary<ResourceKey, ResourceEntry> _index = new Dictionary<ResourceKey, ResourceEntry>();
        private readonly HashSet<ResourceEntry> _owned = new HashSet<ResourceEntry>();
        private readonly List<ResourceResidue> _residues = new List<ResourceResidue>();
        private readonly IExecutionContext _context;

        public ResourceCache(IExecutionContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary> 命中复用索引则在同一把锁内取得一份持有 </summary>
        public bool TryAcquire(ResourceKey key, out ResourceEntry entry)
        {
            lock (_gate)
            {
                if (_index.TryGetValue(key, out entry) && !entry.Unloading)
                {
                    entry.Holds++;
                    return true;
                }
                entry = null;
                return false;
            }
        }

        /// <summary>
        /// 登记新产物 (持有数 0：谁要用谁再 TryRetain)。同键旧记录撤下索引但保留所有权，旧引用继续指向旧对象。
        /// </summary>
        public ResourceEntry Register(ResourceKey key, IResource product)
        {
            if (product == null) throw new ArgumentNullException(nameof(product));
            var entry = new ResourceEntry(key, product);
            lock (_gate)
            {
                if (_index.TryGetValue(key, out var old)) old.Indexed = false;
                _index[key] = entry;
                _owned.Add(entry);
            }
            return entry;
        }

        /// <summary> 对已登记记录追加一份持有；释放责任已被取走则失败 </summary>
        public bool TryRetain(ResourceEntry entry)
        {
            lock (_gate)
            {
                if (entry.Unloading) return false;
                entry.Holds++;
                return true;
            }
        }

        /// <summary> 归还一份持有；任意线程，不阻塞 </summary>
        public void Release(ResourceEntry entry)
        {
            lock (_gate)
            {
                if (entry.Holds > 0) entry.Holds--;
            }
        }

        /// <summary>
        /// 卸载业务选中的、当前无人持有的产物 (含已撤下索引的旧产物和上次失败的残留)。
        /// 取得释放责任后才执行释放；失败的记为残留，不影响其他项，也不封锁资源键。
        /// </summary>
        public async Task<UnloadResult> UnloadAsync(Func<ResourceKey, bool> selector = null)
        {
            var result = new UnloadResult();
            var taken = new List<ResourceEntry>();
            lock (_gate)
            {
                foreach (var entry in _owned)
                {
                    if (selector != null && !selector(entry.Key)) continue;
                    if (entry.Holds > 0) { result.SkippedInUse.Add(entry.Key); continue; }
                    entry.Unloading = true;
                    taken.Add(entry);
                }
                foreach (var entry in taken) Forget(entry);
                for (var i = _residues.Count - 1; i >= 0; i--)
                {
                    if (selector != null && !selector(_residues[i].Key)) continue;
                    taken.Add(_residues[i].Entry);
                    _residues.RemoveAt(i);
                }
            }

            foreach (var entry in taken)
            {
                try
                {
                    await _context.RunAsync(entry.Product.Release).ConfigureAwait(false);
                    result.Released.Add(entry.Key);
                }
                catch (Exception error)
                {
                    var residue = new ResourceResidue(entry, error);
                    lock (_gate) _residues.Add(residue);
                    result.Failed.Add(residue);
                }
            }
            return result;
        }

        /// <summary> 登记一份释放失败的产物 (未登记过的中间产物或迟到产物)；下次卸载再试 </summary>
        internal void KeepResidue(ResourceKey key, IResource product, Exception error)
        {
            var entry = new ResourceEntry(key, product) { Indexed = false, Unloading = true };
            lock (_gate) _residues.Add(new ResourceResidue(entry, error));
        }

        /// <summary> 只读排查：当前所有权集合与残留 </summary>
        public void Snapshot(List<ResourceEntry> entries, List<ResourceResidue> residues)
        {
            lock (_gate)
            {
                entries?.AddRange(_owned);
                residues?.AddRange(_residues);
            }
        }

        private void Forget(ResourceEntry entry)
        {
            _owned.Remove(entry);
            if (entry.Indexed && _index.TryGetValue(entry.Key, out var current) && current == entry) _index.Remove(entry.Key);
            entry.Indexed = false;
        }
    }
}
