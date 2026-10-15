/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 业务持有的资源引用凭证。只持有和归还一份持有，不调度加载或卸载；
 *                持有计数的唯一事实保存在产物记录 (ResourceEntry) 里。
 *                归还始终投递回状态串行域：不在调用者线程上同步阻塞等主线程。
 *                投递后引用对业务立即失效；实际记账稍后在串行域完成 (UnloadUnusedAsync 可能因此
 *                短暂看到该项仍被持有而跳过，这是可接受的保守方向)。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 资源引用凭证：`Value` 会做存活检查 (是引擎调用)，只在状态串行域内访问，其他线程 fail fast；
    /// `IsValid` 是不触碰引擎的纯查询。Dispose 幂等，可任意线程调用。
    /// </summary>
    public sealed class ResourceRef<T> : IDisposable where T : class
    {
        private readonly IExecutionContext _context;
        private readonly ResourceEntry _entry;
        private readonly T _value;
        private readonly long _leaseId;
        private int _disposed;

        internal ResourceRef(IExecutionContext context, ResourceEntry entry, T value, long leaseId)
        {
            _context = context;
            _entry = entry;
            _value = value;
            _leaseId = leaseId;
        }

        /// <summary> 资源对象；在状态串行域外访问立即失败 </summary>
        public T Value
        {
            get
            {
                _context.AssertAccess();
                if (_entry.HoldCount <= 0)
                {
                    throw new ResourceLoadException(_Invalidated());
                }
                return _value;
            }
        }

        /// <summary> 该引用是否仍然有效；不触碰引擎对象，可在任意线程读取 </summary>
        public bool IsValid => Volatile.Read(ref _disposed) == 0 && _entry.HoldCount > 0;

        /// <summary>
        /// 追加一份独立持有。取得持有属于提交类操作：条件判断与记账必须原子完成，
        /// 因此返回可等待结果；状态串行域内同步完成，域外不阻塞调用线程。
        /// </summary>
        public Task<ResourceRef<T>> RetainAsync()
        {
            return _context.RunAsync(() =>
            {
                if (Volatile.Read(ref _disposed) != 0 || !_entry.TryRetain())
                {
                    throw new ResourceLoadException(_Invalidated());
                }
                return new ResourceRef<T>(_context, _entry, _value, _leaseId);
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_context.IsCurrent)
            {
                _entry.ReleaseHold();
                return;
            }
            // 后台线程归还：投递即走，不等待主线程完成记账。
            _context.Post(() => _entry.ReleaseHold());
        }

        private LoadError _Invalidated() => new LoadError(
            DiagnosticCodes.AssetInvalidated, LoadStage.LoadAsset, CleanupStatus.Complete, null,
            new Dictionary<string, object> { { "key", _entry.Key.ToString() } });
    }
}
