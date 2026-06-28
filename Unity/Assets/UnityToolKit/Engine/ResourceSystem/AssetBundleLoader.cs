/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : Unity AssetBundle 加载器。实现 ToolKit 抽象层 ILoader。
 *                地址格式: "bundlePath::assetName"
 *                  bundlePath —— bundle 文件的本地路径 (可由 RemoteFileLoader 先下载到缓存);
 *                  assetName  —— bundle 内的资源名。
 *                内部维护 bundle 级引用计数: 同一 bundle 多个资源共享一次加载, 全部释放后才 Unload。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityToolKit.Engine.ResourceSystem
{
    public sealed class AssetBundleLoader : ILoader
    {
        /// <summary> 地址分隔符: bundlePath::assetName </summary>
        public const string Separator = "::";

        private sealed class BundleRef
        {
            public AssetBundle Bundle;
            public int RefCount;
            public Task<AssetBundle> Loading; // 加载中任务, 防并发重复加载
        }

        private readonly Dictionary<string, BundleRef> _bundles = new Dictionary<string, BundleRef>();
        private readonly object _gate = new object();

        public ELoadType LoadType => ELoadType.AssetBundle;

        public bool CanLoad(string address)
        {
            return !string.IsNullOrEmpty(address) && address.Contains(Separator);
        }

        public async Task<IAssetHandle> LoadAsync(
            string address,
            CancellationToken cancellationToken = default)
        {
            var handle = new AssetHandle(address);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var idx = address.IndexOf(Separator, StringComparison.Ordinal);
                if (idx < 0)
                {
                    handle.SetFailed(new ArgumentException($"AssetBundle 地址格式错误, 应为 bundlePath{Separator}assetName: {address}"));
                    return handle;
                }

                var bundlePath = address.Substring(0, idx);
                var assetName = address.Substring(idx + Separator.Length);

                var bundle = await _AcquireBundleAsync(bundlePath, cancellationToken).ConfigureAwait(true);
                if (bundle == null)
                {
                    handle.SetFailed(new Exception($"AssetBundle 加载失败: {bundlePath}"));
                    return handle;
                }

                var asset = await _LoadAssetAsync(bundle, assetName, cancellationToken).ConfigureAwait(true);
                if (asset == null)
                {
                    _ReleaseBundle(bundlePath); // 资源不存在, 回退 bundle 引用
                    handle.SetFailed(new Exception($"Bundle 内找不到资源: {assetName} @ {bundlePath}"));
                    return handle;
                }

                // 卸载: 释放该资源时, 递减 bundle 引用, 归零则 Unload(false)
                handle.SetSucceed(asset, () => _ReleaseBundle(bundlePath));
            }
            catch (OperationCanceledException)
            {
                handle.SetCancelled();
            }
            catch (Exception e)
            {
                handle.SetFailed(e);
            }

            return handle;
        }

        #region Bundle 引用管理

        private async Task<AssetBundle> _AcquireBundleAsync(
            string bundlePath, CancellationToken cancellationToken)
        {
            Task<AssetBundle> loading;
            lock (_gate)
            {
                if (!_bundles.TryGetValue(bundlePath, out var entry))
                {
                    entry = new BundleRef();
                    _bundles.Add(bundlePath, entry);
                }
                entry.RefCount++;

                if (entry.Bundle != null)
                {
                    return entry.Bundle; // 已加载
                }
                if (entry.Loading == null)
                {
                    entry.Loading = _LoadBundleAsync(bundlePath, cancellationToken);
                }
                loading = entry.Loading;
            }

            var bundle = await loading.ConfigureAwait(true);

            lock (_gate)
            {
                if (_bundles.TryGetValue(bundlePath, out var entry))
                {
                    entry.Bundle = bundle;
                    entry.Loading = null;
                    if (bundle == null)
                    {
                        // 加载失败, 回退本次引用
                        entry.RefCount--;
                        if (entry.RefCount <= 0)
                        {
                            _bundles.Remove(bundlePath);
                        }
                    }
                }
            }
            return bundle;
        }

        private void _ReleaseBundle(string bundlePath)
        {
            AssetBundle toUnload = null;
            lock (_gate)
            {
                if (!_bundles.TryGetValue(bundlePath, out var entry))
                {
                    return;
                }
                entry.RefCount--;
                if (entry.RefCount <= 0)
                {
                    toUnload = entry.Bundle;
                    _bundles.Remove(bundlePath);
                }
            }
            // Unload(false): 卸载 bundle 自身, 但不销毁已实例化/已引用的资源
            toUnload?.Unload(false);
        }

        #endregion

        #region Unity 异步操作 -> Task

        private static Task<AssetBundle> _LoadBundleAsync(
            string bundlePath, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<AssetBundle>();
            var request = AssetBundle.LoadFromFileAsync(bundlePath);
            request.completed += _ =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }
                tcs.TrySetResult(request.assetBundle);
            };
            return tcs.Task;
        }

        private static Task<Object> _LoadAssetAsync(
            AssetBundle bundle, string assetName, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<Object>();
            var request = bundle.LoadAssetAsync<Object>(assetName);
            request.completed += _ =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }
                tcs.TrySetResult(request.asset);
            };
            return tcs.Task;
        }

        #endregion
    }
}
