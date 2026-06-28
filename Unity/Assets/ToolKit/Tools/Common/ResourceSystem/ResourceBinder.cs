/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : ResourceBinder 实现 (引擎无关)。基于 ResourceManager 加载, 以 target 为单位用
 *                generation(代号) 处理并发时序:
 *                  - 每次 ApplyAsync 自增该 target 的 generation, 即作废之前的请求;
 *                  - 加载完成后比对 generation, 不是最新则丢弃结果(释放凭证), 不应用;
 *                  - 应用成功后释放该 target 上一凭证, 切换到新凭证 (同地址重复也不会泄漏)。
 *                内部统一持有业务凭证 ResourceRef, 与一般业务走同一套引用账目。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public sealed class ResourceBinder
    {
        private sealed class Binding
        {
            public long Generation;
            public string Address;
            public ResourceRef Ref;
        }

        // 以引用相等比较 target
        private sealed class RefComparer : IEqualityComparer<object>
        {
            public static readonly RefComparer Instance = new RefComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private readonly ResourceManager _manager;
        private readonly Dictionary<object, Binding> _bindings = new Dictionary<object, Binding>(RefComparer.Instance);
        private readonly object _gate = new object();

        public ResourceBinder(ResourceManager manager)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        }

        /// <summary>
        /// 加载资源并应用到 target 上 (一步到位)。
        /// <para>同一 target 再次调用会作废上一次未完成/已完成的绑定 —— 以最后一次为准。</para>
        /// </summary>
        /// <typeparam name="TTarget">应用目标类型 (如 Image)</typeparam>
        /// <typeparam name="TResource">资源类型 (如 Sprite / byte[])</typeparam>
        /// <param name="target">应用目标</param>
        /// <param name="address">资源地址</param>
        /// <param name="applicable">如何把资源应用到目标</param>
        /// <param name="loadType">加载来源</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task ApplyAsync<TTarget, TResource>(
            TTarget target, string address, IApplicable applicable,
            ELoadType loadType = ELoadType.Auto, CancellationToken cancellationToken = default) where TTarget : class where TResource : class
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (applicable == null) throw new ArgumentNullException(nameof(applicable));

            var binding = _GetOrCreate(target);

            // 自增 generation -> 作废此前未完成的请求
            long gen;
            lock (binding)
            {
                gen = ++binding.Generation;
            }

            ResourceRef refObj;
            try
            {
                refObj = await _manager.LoadRefAsync(address, loadType, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // 加载中被取消: 不应用, 保留 target 原有资源
            }

            lock (binding)
            {
                // 期间又发起了新请求 / 已 Unbind -> 本次作废
                if (gen != binding.Generation)
                {
                    refObj?.Dispose();
                    return;
                }

                if (refObj == null || !refObj.IsValid)
                {
                    refObj?.Dispose();
                    return;
                }

                // 应用到目标
                var resource = refObj.Get<TResource>();
                applicable.Apply<TTarget, TResource>(target, resource);

                // 释放旧凭证, 切换到新凭证 (旧 == 新地址时也安全: 多出的那次引用被这里释放)
                var old = binding.Ref;
                binding.Ref = refObj;
                binding.Address = address;
                old?.Dispose();
            }
        }

        /// <summary>
        /// 单纯取消应用: 只作废 target 进行中(尚未完成)的加载请求, 使其加载完成后不再应用。
        /// <para>不影响 target 当前已应用的资源, 也不释放其句柄 —— 与 Unbind 的区别正在于此。</para>
        /// </summary>
        public void CancelApply(object target)
        {
            if (target == null)
            {
                return;
            }

            Binding binding;
            lock (_gate)
            {
                if (!_bindings.TryGetValue(target, out binding))
                {
                    return;
                }
            }

            // 仅自增 generation 作废进行中的请求; 保留当前已应用资源与句柄不动
            lock (binding)
            {
                binding.Generation++;
            }
        }

        /// <summary>
        /// 解除 target 的绑定: 作废其进行中的请求, 并释放它当前持有的资源句柄。
        /// </summary>
        public void Unbind(object target)
        {
            if (target == null)
            {
                return;
            }

            Binding binding;
            lock (_gate)
            {
                if (!_bindings.TryGetValue(target, out binding))
                {
                    return;
                }
                _bindings.Remove(target);
            }

            ResourceRef refObj;
            lock (binding)
            {
                binding.Generation++; // 作废进行中的请求
                refObj = binding.Ref;
                binding.Ref = null;
            }
            refObj?.Dispose();
        }

        private Binding _GetOrCreate(object target)
        {
            lock (_gate)
            {
                if (!_bindings.TryGetValue(target, out var binding))
                {
                    binding = new Binding();
                    _bindings.Add(target, binding);
                }
                return binding;
            }
        }
    }
}
