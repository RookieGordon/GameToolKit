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

        /// <summary> 可从任意线程申请；应用和状态提交回到执行上下文。后发请求作废在途申请，取消/失败保留当前绑定。 </summary>
        public async Task<BindingResult> ApplyAsync<TTarget, TResource>(
            TTarget target,
            string slotId,
            string address,
            IResourceApplicator<TTarget, TResource> applicator,
            ResourceSource source,
            RequestOptions? options = null,
            CancellationToken cancellationToken = default)
            where TTarget : class
            where TResource : class
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrEmpty(slotId)) throw new ArgumentException("slotId 不能为空", nameof(slotId));
            if (applicator == null) throw new ArgumentNullException(nameof(applicator));

            using var waitCts = new CancellationTokenSource();
            Slot? slot = null;
            ResourceRef<TResource>? candidate = null;
            try
            {
                await _context.RunAsync(() =>
                {
                    slot = _GetSlotNoLock(target, slotId);
                    if (slot.Mutating)
                        throw new InvalidOperationException($"slot {slotId} 正在应用或解除绑定，不能重入申请");
                    var previous = slot.PendingWaiter;
                    slot.PendingWaiter = waitCts;
                    // 先登记再通知旧请求；取消回调抛错也由本次 finally 撤下登记。
                    previous?.Cancel();
                }).ConfigureAwait(false);

                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, waitCts.Token);
                    candidate = await _manager.LoadAsync<TResource>(address, source, options, linked.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                        && waitCts.IsCancellationRequested)
                {
                    // 被更新申请作废：快速回收在途加载，但不把取消当作错误上抛 (E09)
                    return BindingResult.Superseded;
                }

                return await _context.RunAsync(() =>
                {
                    if (slot!.Mutating)
                    {
                        throw new InvalidOperationException(
                            $"slot {slotId} 正在应用中，同步重入被 Mutating 门闩拒绝");
                    }
                    if (waitCts.IsCancellationRequested) return BindingResult.Superseded;
                    slot.Mutating = true;
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
                        slot.Mutating = false;
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                if (slot != null)
                    await _context.RunAsync(() =>
                    {
                        if (ReferenceEquals(slot.PendingWaiter, waitCts)) slot.PendingWaiter = null;
                    }).ConfigureAwait(false);
                candidate?.Dispose(); // 应用抛错或被取代也不能泄漏新资源
            }
        }

        /// <summary> 在执行上下文内同步作废 slot 的在途申请；Unity 下要求主线程调用。 </summary>
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
                slot.PendingWaiter?.Cancel();
            });
        }

        /// <summary>
        /// 解除绑定：作废在途申请 → 清除目标属性 (先解绑) → 释放当前持有 → 移除记录。
        /// 清除失败保留记录与引用并抛出；与 Apply/Cancel 互斥由同一 Mutating 门闩保证。
        /// 这是同步 API，Unity 下要求主线程调用；清除失败直接交给调用者处理。
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
                if (slot.Mutating)
                {
                    throw new InvalidOperationException($"slot {slotId} 正在应用中，Revert 被拒绝");
                }
                slot.Mutating = true;
                IDisposable? current;
                try
                {
                    slot.PendingWaiter?.Cancel();
                    applicator.Revert(target); // 先解除目标对资源的引用
                    current = slot.CurrentRef;
                    slot.CurrentRef = null;
                    _RemoveSlotNoLock(target, slotId);
                }
                finally
                {
                    slot.Mutating = false;
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
