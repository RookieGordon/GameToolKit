/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 已加载产物的容器：保存条目、引用数量和空闲索引。
 *                调用方将所有读写放在同一个执行上下文内，保证组合操作的原子性。
 *                本类只同步维护记录；加载、取消、卸载和完成通知由 LoadManager 组织。
 */

using System;
using System.Collections.Generic;

namespace ToolKit.Tools.Common
{
    internal sealed class ResourceStore
    {
        private readonly Dictionary<ResourceKey, ResourceEntry> _entries = new Dictionary<ResourceKey, ResourceEntry>();
        private readonly LinkedList<ResourceEntry> _idleLru = new LinkedList<ResourceEntry>(); // 头 = 最新

        #region 条目登记与移除

        internal bool TryGetEntry(ResourceKey key, out ResourceEntry entry)
        {
            return _entries.TryGetValue(key, out entry!);
        }

        internal void AddEntry(ResourceEntry entry)
        {
            _entries.Add(entry.Key, entry);
        }

        /// <summary> 同键的旧条目不能修改后来登记的新条目。 </summary>
        internal bool ContainsEntry(ResourceEntry entry)
        {
            return _entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry);
        }

        /// <summary> 只移除记录；旧条目的完成通知由加载流程随后发出。 </summary>
        internal void RemoveEntry(ResourceEntry entry)
        {
            if (ContainsEntry(entry))
            {
                _entries.Remove(entry.Key);
            }
            RemoveIdle(entry);
            entry.State = ResourceState.Removed;
        }

        internal List<ResourceEntry> SnapshotEntries()
        {
            return new List<ResourceEntry>(_entries.Values);
        }

        #endregion

        #region 产物查询与状态登记

        /// <summary> 检查这份具体产物，不把旧引用重新绑定到同键的新产物。 </summary>
        internal bool IsLive(ResourceEntry entry)
        {
            if (!ContainsEntry(entry)
                || (entry.State != ResourceState.Ready && entry.State != ResourceState.Idle)
                || entry.IsAssetInvalid) return false;
            if (IsAssetAlive(entry.Asset)) return true;
            entry.IsAssetInvalid = true;
            RemoveIdle(entry);
            return false;
        }

        internal static bool IsAssetAlive(LoadedAsset asset)
        {
            try { return asset.Value != null && asset.IsAlive(); }
            catch { return false; }
        }

        internal void MarkUnloading(ResourceEntry entry)
        {
            RemoveIdle(entry);
            entry.State = ResourceState.Unloading;
        }

        internal void MarkReleaseFailed(ResourceEntry entry, LoadError error)
        {
            entry.State = ResourceState.ReleaseFailed;
            entry.CleanupError = error;
        }

        #endregion

        #region 引用数量

        /// <summary> 登记一次持有；空闲资源再次被持有时同时移出空闲索引。 </summary>
        internal void AddReference(ResourceEntry entry)
        {
            if (entry.State == ResourceState.Idle)
            {
                RemoveIdle(entry);
                entry.State = ResourceState.Ready;
            }
            entry.HoldCount++;
        }

        /// <summary> 归还一次持有，返回剩余数量；归零后的处理由加载管理器决定。 </summary>
        internal int RemoveReference(ResourceEntry entry)
        {
            if (entry.HoldCount <= 0)
            {
                throw new InvalidOperationException($"资源引用数量不能小于零: {entry.Key}");
            }
            entry.HoldCount--;
            return entry.HoldCount;
        }

        #endregion

        #region 空闲索引

        internal void AddIdle(ResourceEntry entry, double now)
        {
            RemoveIdle(entry);
            entry.State = ResourceState.Idle;
            entry.IdleSince = now;
            entry.IdleNode = _idleLru.AddFirst(entry);
        }

        /// <summary> 只移出空闲索引，不替调用者决定条目的下一状态。 </summary>
        internal void RemoveIdle(ResourceEntry entry)
        {
            if (entry.IdleNode != null)
            {
                _idleLru.Remove(entry.IdleNode);
                entry.IdleNode = null;
            }
        }

        /// <summary> 从最久未使用到最近使用；调用者可以在遍历快照时移除实际条目。 </summary>
        internal List<ResourceEntry> SnapshotIdleEntries()
        {
            var entries = new List<ResourceEntry>(_idleLru.Count);
            for (var node = _idleLru.Last; node != null; node = node.Previous)
            {
                entries.Add(node.Value);
            }
            return entries;
        }

        internal ResourceEntry? OldestIdle => _idleLru.Last?.Value;

        internal int IdleCount => _idleLru.Count;

        internal long EstimatedIdleBytes
        {
            get
            {
                var bytes = 0L;
                foreach (var entry in _idleLru)
                {
                    bytes += entry.EstimatedBytes;
                }
                return bytes;
            }
        }

        #endregion

        #region 状态查询

        internal List<LoadError> CollectStuckErrors()
        {
            var errors = new List<LoadError>();
            foreach (var entry in _entries.Values)
            {
                if (entry.State == ResourceState.ReleaseFailed && entry.CleanupError != null)
                {
                    errors.Add(entry.CleanupError);
                }
            }
            return errors;
        }

        #endregion
    }
}
