/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 已加载产物的容器：保存具体产物、引用数量和按键复用索引。
 *                所有读写放在同一个执行上下文内；本类不调度加载或卸载。
 */

using System;
using System.Collections.Generic;

namespace ToolKit.Tools.Common
{
    internal sealed class ResourceStore
    {
        private readonly Dictionary<ResourceKey, ResourceEntry> _current = new Dictionary<ResourceKey, ResourceEntry>();
        // 失效旧产物仍由原引用/业务卸载持有，但不再阻止同键的新产物登记。
        private readonly HashSet<ResourceEntry> _entries = new HashSet<ResourceEntry>();

        internal bool TryGetEntry(ResourceKey key, out ResourceEntry entry) => _current.TryGetValue(key, out entry!);

        internal void AddEntry(ResourceEntry entry)
        {
            _current.Add(entry.Key, entry);
            _entries.Add(entry);
        }

        internal bool ContainsEntry(ResourceEntry entry) => _entries.Contains(entry);

        /// <summary> 只取消按键复用；仍保留具体产物及其引用，等待业务卸载。 </summary>
        internal void DetachEntry(ResourceEntry entry)
        {
            if (_current.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                _current.Remove(entry.Key);
        }

        internal void RemoveEntry(ResourceEntry entry)
        {
            DetachEntry(entry);
            _entries.Remove(entry);
            entry.State = ResourceState.Removed;
        }

        internal List<ResourceEntry> SnapshotEntries() => new List<ResourceEntry>(_entries);

        /// <summary> 查询具体产物的有效性；失效只取消复用，不触发任何释放。 </summary>
        internal bool IsLive(ResourceEntry entry)
        {
            if (!ContainsEntry(entry)
                || (entry.State != ResourceState.Ready && entry.State != ResourceState.Idle)
                || entry.IsAssetInvalid) return false;
            if (IsAssetAlive(entry.Asset)) return true;
            entry.IsAssetInvalid = true;
            DetachEntry(entry);
            return false;
        }

        internal static bool IsAssetAlive(LoadedAsset asset)
        {
            try { return asset.Value != null && asset.IsAlive(); }
            catch { return false; }
        }

        internal void AddReference(ResourceEntry entry)
        {
            entry.State = ResourceState.Ready;
            entry.HoldCount++;
        }

        internal int RemoveReference(ResourceEntry entry)
        {
            if (entry.HoldCount <= 0)
                throw new InvalidOperationException($"资源引用数量不能小于零: {entry.Key}");
            return --entry.HoldCount;
        }
    }
}
