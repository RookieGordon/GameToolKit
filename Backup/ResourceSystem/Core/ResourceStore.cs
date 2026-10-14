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
        private readonly IResourceDiagnostics _diagnostics;
        private readonly Func<bool> _isDisposed;
        private long _leaseSeed;

        internal IExecutionContext Context { get; }

        internal ResourceStore(IExecutionContext context, IResourceDiagnostics diagnostics, Func<bool> isDisposed)
        {
            Context = context;
            _diagnostics = diagnostics;
            _isDisposed = isDisposed;
        }

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

        /// <summary> 加载流程已确认产物存活，在同一次状态提交中建立各调用者的独立持有。 </summary>
        internal ResourceRef<T> CreateReference<T>(ResourceEntry entry) where T : class
        {
            if (!(entry.Asset.Value is T))
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetTypeMismatch, LoadStage.LoadAsset, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "key", entry.Key.ToString() },
                        { "requestedType", typeof(T).Name },
                        { "actualType", entry.Asset.Value.GetType().Name },
                    }));
            return AddReference<T>(entry);
        }

        internal object GetLiveAsset(ResourceEntry entry, long leaseId)
        {
            if (_isDisposed()) throw new ObjectDisposedException(nameof(ResourceManager));
            if (!IsLive(entry))
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetInvalidated, LoadStage.LoadAsset, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "key", entry.Key.ToString() }, { "leaseId", leaseId } }));
            return entry.Asset.Value;
        }

        internal bool IsReferenceValid(ResourceEntry entry) => !_isDisposed() && IsLive(entry);

        internal ResourceRef<T> Retain<T>(ResourceEntry entry, long leaseId) where T : class
        {
            GetLiveAsset(entry, leaseId);
            return AddReference<T>(entry);
        }

        /// <summary> 给已有所有者增加一份持有；清理残留也需要保护失效产物，不重新查询后端。 </summary>
        internal ResourceRef<T> AddReference<T>(ResourceEntry entry) where T : class
        {
            entry.State = ResourceState.Ready;
            entry.HoldCount++;
            return new ResourceRef<T>(this, entry, ++_leaseSeed);
        }

        internal void ReleaseReference(ResourceEntry entry, long leaseId)
        {
            // 退出释放不等待引用归还；晚归还不再改变已退出的记录。
            if (_isDisposed()) return;
            if (entry.HoldCount <= 0)
            {
                _diagnostics.Report(new LoadError(
                    DiagnosticCodes.InternalConsistency, LoadStage.ReleaseAsset, CleanupStatus.Unknown, null,
                    new Dictionary<string, object> { { "key", entry.Key.ToString() }, { "leaseId", leaseId } }));
                return;
            }
            if (--entry.HoldCount == 0) entry.State = ResourceState.Idle;
        }
    }
}
