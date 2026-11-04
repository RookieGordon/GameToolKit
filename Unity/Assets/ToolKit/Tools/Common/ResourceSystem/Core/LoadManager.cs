/*
 * description  : 一个来源的加载编排。同键多等一与并发名额交给 WorkCenter；
 *                一轮实际加载 = 调加载器 → 必要时转换 → 登记产物到缓存。
 *                个人取消/超时只结束本人；一轮后端失败只结算一个承担者，其余等待者自动进入下一轮 (需求 Q01)。
 *                最后一个等待者退出时取消本轮；后端停不下来而迟到的产物由本轮直接释放。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    public sealed class LoadManager
    {
        private readonly ILoader _loader;
        private readonly LoaderPolicy _policy;
        private readonly ResourceCache _cache;
        private readonly IReadOnlyDictionary<Type, IProductConverter> _converters;
        private readonly IExecutionContext _context;
        private readonly WorkCenter<ResourceKey, Outcome> _center;

        public LoadManager(ILoader loader, LoaderPolicy policy, ResourceCache cache,
            IReadOnlyDictionary<Type, IProductConverter> converters, IExecutionContext context, Action<Exception> diagnose = null)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _policy = policy;
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _converters = converters ?? throw new ArgumentNullException(nameof(converters));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _center = new WorkCenter<ResourceKey, Outcome>(RunRoundAsync,
                policy.MaxConcurrentLoads, policy.MaxConcurrentLoads, NoWaitersPolicy.CancelExecution, null, diagnose);
        }

        /// <summary>
        /// 取得一份持有：命中缓存直接返回；否则加入或发起一轮实际加载。返回的记录已含本请求的一份持有。
        /// token 只结束本人的等待；超时按来源策略计，覆盖排队、共享等待与接续。
        /// </summary>
        public async Task<ResourceEntry> AcquireAsync(ResourceKey key, IProgress<OperationProgress> progress, CancellationToken token)
        {
            using (var budget = _policy.LoadTimeout.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(token) : null)
            {
                var budgetToken = token;
                if (budget != null)
                {
                    budget.CancelAfter(_policy.LoadTimeout.Value);
                    budgetToken = budget.Token;
                }
                try
                {
                    while (true)
                    {
                        if (_cache.TryAcquire(key, out var hit)) return hit;

                        var outcome = await _center.GetAsync(key, progress, budgetToken).ConfigureAwait(false);
                        if (outcome.Error != null)
                        {
                            if (outcome.TryClaimFailure()) throw outcome.Error; // 本人承担这一轮的失败
                            continue;                                          // 他人已承担：本人接续下一轮
                        }
                        if (_cache.TryRetain(outcome.Entry)) return outcome.Entry;
                        // 登记与取得持有之间产物已被业务卸载：当作未命中再来一轮
                    }
                }
                catch (OperationCanceledException) when (budgetToken.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    throw ResourceException.Of(DiagnosticCodes.RequestTimeout, LoadStage.WaitForLoad, key);
                }
            }
        }

        /// <summary> 拒绝新请求，在途加载照常做完 (被替换下来的加载器走这里) </summary>
        public Task CompleteAsync() => _center.CompleteAsync();

        /// <summary> 拒绝新请求，取消在途加载并等待其真正退出 </summary>
        public Task StopAsync() => _center.StopAsync();

        private async Task<Outcome> RunRoundAsync(ResourceKey key, IProgress<OperationProgress> progress, CancellationToken roundToken)
        {
            try
            {
                var product = await _loader.LoadAsync(key, _context, progress, roundToken).ConfigureAwait(false);
                if (product == null || product.Value == null)
                {
                    await DiscardAsync(key, product).ConfigureAwait(false);
                    throw ResourceException.Of(DiagnosticCodes.LoaderInvalidResult, LoadStage.LoadAsset, key);
                }
                if (!key.RequestedType.IsInstanceOfType(product.Value))
                    product = await ConvertAsync(product, key, roundToken).ConfigureAwait(false);

                if (roundToken.IsCancellationRequested)
                {
                    // 迟到结果：本轮已无接收者，产物由本轮负责清理
                    await DiscardAsync(key, product).ConfigureAwait(false);
                    throw new OperationCanceledException(roundToken);
                }
                return Outcome.Success(_cache.Register(key, product));
            }
            catch (OperationCanceledException) when (roundToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                return Outcome.Failure(error is ResourceException ? error
                    : ResourceException.Of(DiagnosticCodes.AssetLoadFailed, LoadStage.LoadAsset, key, error));
            }
        }

        private async Task<IResource> ConvertAsync(IResource source, ResourceKey key, CancellationToken token)
        {
            if (!_converters.TryGetValue(key.RequestedType, out var converter))
            {
                await DiscardAsync(key, source).ConfigureAwait(false);
                throw ResourceException.Of(DiagnosticCodes.AssetTypeMismatch, LoadStage.Convert, key);
            }

            IResource converted;
            try
            {
                converted = await _context.InvokeAsync(() => converter.ConvertAsync(source, key, token)).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await DiscardAsync(key, source).ConfigureAwait(false);
                if (error is OperationCanceledException || error is ResourceException) throw;
                throw ResourceException.Of(DiagnosticCodes.AssetConversionFailed, LoadStage.Convert, key, error);
            }

            await DiscardAsync(key, source).ConfigureAwait(false); // 中间产物用完即释放
            if (converted == null || converted.Value == null || !key.RequestedType.IsInstanceOfType(converted.Value))
            {
                if (converted != null) await DiscardAsync(key, converted).ConfigureAwait(false);
                throw ResourceException.Of(DiagnosticCodes.AssetTypeMismatch, LoadStage.Convert, key);
            }
            return converted;
        }

        /// <summary> 释放不再需要的产物；释放失败的保留为残留，不吞掉原因 </summary>
        private async Task DiscardAsync(ResourceKey key, IResource product)
        {
            if (product == null) return;
            try
            {
                await _context.RunAsync(product.Release).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _cache.KeepResidue(key, product, error);
            }
        }

        /// <summary> 一轮的结果：成功的记录，或由恰好一个等待者承担的失败 </summary>
        private sealed class Outcome
        {
            public ResourceEntry Entry;
            public Exception Error;
            private int _claimed;

            public static Outcome Success(ResourceEntry entry) => new Outcome { Entry = entry };
            public static Outcome Failure(Exception error) => new Outcome { Error = error };

            public bool TryClaimFailure() => Interlocked.Exchange(ref _claimed, 1) == 0;
        }
    }
}
