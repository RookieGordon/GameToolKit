/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 泛型资源绑定器 (P6, §12.1)。以 (目标引用, SlotId) 为键：同一 Image 的 sprite
 *                与同一 Renderer 的 material 是不同 slot。后发请求作废未完成申请 (Superseded 不弹错)；
 *                应用器必须异常安全 (Replace 失败旧属性保持不变)；切换成功后才释放旧持有；
 *                Revert 先清除目标属性再释放底层引用；Mutating 门闩拒绝同步重入同一 slot。
 */

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public enum BindingResult
    {
        Applied,
        Superseded,
    }

    /// <summary>
    /// 应用器契约：Replace 必须具备异常安全保证 —— 失败时旧属性保持不变；
    /// 需要设置多个属性时先保存旧状态，全部成功才视为成功，失败恢复旧状态后再抛。
    /// </summary>
    public interface IResourceApplicator<in TTarget, in TResource> where TTarget : class where TResource : class
    {
        void Replace(TTarget target, TResource value);

        void Revert(TTarget target);
    }

    public sealed class ResourceBinder
    {
        private sealed class Slot
        {
            public long Generation;
            public IDisposable? CurrentRef;
            public bool Mutating;
            public CancellationTokenSource? PendingWaiter;
        }

        private sealed class RefComparer : IEqualityComparer<object>
        {
            public static readonly RefComparer Instance = new RefComparer();
            bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private readonly ResourceManager _manager;
        private readonly IExecutionContext _context;
        private readonly Dictionary<object, Dictionary<string, Slot>> _bindings =
            new Dictionary<object, Dictionary<string, Slot>>(RefComparer.Instance);

        public ResourceBinder(ResourceManager manager, IExecutionContext? context = null)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _context = context ?? ImmediateExecutionContext.Instance;
        }

        /// <summary> 申请并应用：同一 slot 的后发请求作废在途申请；取消/失败保留当前绑定 </summary>
        public async Task<BindingResult> ApplyAsync<TTarget, TResource>(
            TTarget target,
            string slotId,
            string address,
            IResourceApplicator<TTarget, TResource> applicator,
            string? loader = null,
            RequestOptions? options = null,
            CancellationToken cancellationToken = default)
            where TTarget : class
            where TResource : class
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrEmpty(slotId)) throw new ArgumentException("slotId 不能为空", nameof(slotId));
            if (applicator == null) throw new ArgumentNullException(nameof(applicator));

            var slot = _context.Invoke(() => _GetSlotNoLock(target, slotId));
            var generation = 0L;
            var waitCts = new CancellationTokenSource();
            _context.Invoke(() =>
            {
                lock (slot)
                {
                    // 自增 generation 作废此前未完成的申请：作废由完成时的 generation 检查表达，
                    // 不取消在途申请的令牌 (取消会让旧申请抛 OCE 而非返回 Superseded)
                    generation = ++slot.Generation;
                    slot.PendingWaiter?.Cancel();
                    slot.PendingWaiter = waitCts;
                }
            });

            ResourceRef<TResource>? candidate = null;
            try
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, waitCts.Token);
                    candidate = await _manager.LoadAsync<TResource>(address, loader, options, linked.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                        && _IsSuperseded(slot, generation))
                {
                    // 被更新申请作废：快速回收在途加载，但不把取消当作错误上抛 (E09)
                    return BindingResult.Superseded;
                }

                return _context.Invoke(() =>
                {
                    lock (slot)
                    {
                        if (slot.Mutating)
                        {
                            throw new InvalidOperationException(
                                $"slot {slotId} 正在应用中，同步重入被 Mutating 门闩拒绝");
                        }
                        if (generation != slot.Generation)
                        {
                            return BindingResult.Superseded; // 已被更新请求取代：不展示过期错误
                        }
                        slot.Mutating = true;
                    }
                    try
                    {
                        applicator.Replace(target, candidate.Value); // 异常安全：失败旧属性不变
                        var old = slot.CurrentRef;
                        slot.CurrentRef = candidate; // 所有权交给 slot
                        candidate = null;
                        old?.Dispose(); // 切换成功后释放旧持有
                        return BindingResult.Applied;
                    }
                    finally
                    {
                        lock (slot)
                        {
                            slot.Mutating = false;
                        }
                    }
                });
            }
            finally
            {
                _context.Invoke(() =>
                {
                    lock (slot)
                    {
                        if (ReferenceEquals(slot.PendingWaiter, waitCts))
                        {
                            slot.PendingWaiter = null;
                        }
                    }
                });
                waitCts.Dispose();
                candidate?.Dispose(); // 应用抛错或被取代也不能泄漏新资源
            }
        }

        private static bool _IsSuperseded(Slot slot, long generation)
        {
            lock (slot)
            {
                return generation != slot.Generation;
            }
        }

        /// <summary> 只作废 slot 进行中的申请；不影响当前已应用的资源与持有 </summary>
        public void CancelApply(object target, string slotId)
        {
            if (target == null || string.IsNullOrEmpty(slotId))
            {
                return;
            }
            _context.Invoke(() =>
            {
                var slot = _FindSlotNoLock(target, slotId);
                if (slot == null)
                {
                    return;
                }
                lock (slot)
                {
                    slot.Generation++; // 作废在途申请
                    slot.PendingWaiter?.Cancel();
                }
            });
        }

        /// <summary>
        /// 解除绑定：作废在途申请 → 清除目标属性 (先解绑) → 释放当前持有 → 移除记录。
        /// 清除失败保留记录与引用并抛出；与 Apply/Cancel 互斥由同一 Mutating 门闩保证。
        /// </summary>
        public void Revert<TTarget, TResource>(TTarget target, string slotId,
            IResourceApplicator<TTarget, TResource> applicator)
            where TTarget : class
            where TResource : class
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            _context.Invoke(() =>
            {
                var slot = _FindSlotNoLock(target, slotId);
                if (slot == null)
                {
                    return;
                }
                lock (slot)
                {
                    if (slot.Mutating)
                    {
                        throw new InvalidOperationException($"slot {slotId} 正在应用中，Revert 被拒绝");
                    }
                    slot.Generation++;
                    slot.PendingWaiter?.Cancel();
                    slot.Mutating = true;
                }
                IDisposable? current;
                try
                {
                    applicator.Revert(target); // 先解除目标对资源的引用
                    current = slot.CurrentRef;
                    slot.CurrentRef = null;
                    _RemoveSlotNoLock(target, slotId);
                }
                finally
                {
                    lock (slot)
                    {
                        slot.Mutating = false;
                    }
                }
                current?.Dispose(); // 引用解除成功后才释放底层持有
            });
        }

        private Slot _GetSlotNoLock(object target, string slotId)
        {
            if (!_bindings.TryGetValue(target, out var slots))
            {
                slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
                _bindings.Add(target, slots);
            }
            if (!slots.TryGetValue(slotId, out var slot))
            {
                slot = new Slot();
                slots.Add(slotId, slot);
            }
            return slot;
        }

        private Slot? _FindSlotNoLock(object target, string slotId)
        {
            return _bindings.TryGetValue(target, out var slots)
                   && slots.TryGetValue(slotId, out var slot)
                ? slot
                : null;
        }

        private void _RemoveSlotNoLock(object target, string slotId)
        {
            if (_bindings.TryGetValue(target, out var slots))
            {
                slots.Remove(slotId);
                if (slots.Count == 0)
                {
                    _bindings.Remove(target);
                }
            }
        }
    }
}
