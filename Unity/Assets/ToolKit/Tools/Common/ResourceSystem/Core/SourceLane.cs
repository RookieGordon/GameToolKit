/*
 * description  : 一个资源来源的执行通道 = 加载器 + 并发信号量 + 策略 + 转换与清理。
 *                没有常驻工作线程；一次尝试 = 取得来源并发名额 → 在执行上下文内调用加载器 → 必要时转换 → 提交或释放。
 *                无效产物、转换中间产物与未提交成功的迟到产物统一在此通过执行上下文释放，释放失败记入残留。
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common
{
    internal sealed class SourceLane
    {
        private readonly ILoader _loader;
        private readonly SemaphoreSlim _slots;
        private readonly IReadOnlyDictionary<Type, IProductConverter> _converters;
        private readonly IExecutionContext _context;
        private readonly Action<ResourceKey, IResource, Exception> _keepResidue;

        public SourceLane(
            ILoader loader,
            LoaderPolicy policy,
            IReadOnlyDictionary<Type, IProductConverter> converters,
            IExecutionContext context,
            Action<ResourceKey, IResource, Exception> keepResidue)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            Policy = policy;
            _slots = new SemaphoreSlim(policy.MaxConcurrentLoads, policy.MaxConcurrentLoads);
            _converters = converters ?? throw new ArgumentNullException(nameof(converters));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _keepResidue = keepResidue ?? throw new ArgumentNullException(nameof(keepResidue));
        }

        public LoaderPolicy Policy { get; }

        /// <summary> 执行一轮实际生产：限流 → 加载 → 校验 → 转换 → 原子提交；未提交成功则在释放名额前清理产物 </summary>
        public async Task ProduceAsync(
            ResourceKey key,
            IProgress<OperationProgress>? progress,
            CancellationToken cancellation,
            Func<IResource, bool> tryCommit)
        {
            await _slots.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var product = await InvokeLoaderAsync(key, progress, cancellation).ConfigureAwait(false);
                if (cancellation.IsCancellationRequested)
                {
                    await DiscardAsync(key, product).ConfigureAwait(false);
                    return;
                }

                if (!key.RequestedType.IsInstanceOfType(product.Value))
                    product = await ConvertAsync(product, key, cancellation).ConfigureAwait(false);

                if (!tryCommit(product))
                    await DiscardAsync(key, product).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }

        private async Task<IResource> InvokeLoaderAsync(ResourceKey key, IProgress<OperationProgress>? progress, CancellationToken cancellation)
        {
            IResource? product;
            try
            {
                product = await _context.InvokeAsync(() =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return _loader.LoadAsync(key, progress, cancellation);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (ResourceException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw ResourceException.Of(DiagnosticCodes.AssetLoadFailed, LoadStage.LoadAsset, key, error);
            }

            if (product == null || product.Value == null)
            {
                await DiscardAsync(key, product).ConfigureAwait(false);
                throw ResourceException.Of(DiagnosticCodes.LoaderInvalidResult, LoadStage.LoadAsset, key);
            }

            return product;
        }

        private async Task<IResource> ConvertAsync(IResource source, ResourceKey key, CancellationToken cancellation)
        {
            if (!_converters.TryGetValue(key.RequestedType, out var converter))
            {
                await DiscardAsync(key, source).ConfigureAwait(false);
                throw ResourceException.Of(DiagnosticCodes.AssetTypeMismatch, LoadStage.Convert, key);
            }

            IResource? converted;
            try
            {
                converted = await _context.InvokeAsync(() => converter.ConvertAsync(source, key, cancellation)).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await DiscardAsync(key, source).ConfigureAwait(false);
                if (error is OperationCanceledException && cancellation.IsCancellationRequested) throw;
                if (error is ResourceException) throw;
                throw ResourceException.Of(DiagnosticCodes.AssetConversionFailed, LoadStage.Convert, key, error);
            }

            await DiscardAsync(key, source).ConfigureAwait(false);
            if (converted == null || converted.Value == null || !key.RequestedType.IsInstanceOfType(converted.Value))
            {
                await DiscardAsync(key, converted).ConfigureAwait(false);
                throw ResourceException.Of(DiagnosticCodes.AssetTypeMismatch, LoadStage.Convert, key);
            }

            return converted;
        }

        private async Task DiscardAsync(ResourceKey key, IResource? product)
        {
            if (product == null) return;
            try
            {
                await _context.RunAsync(product.Release).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _keepResidue(key, product, error);
            }
        }
    }
}
