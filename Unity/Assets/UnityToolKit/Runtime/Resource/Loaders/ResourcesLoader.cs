/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : Unity Resources 加载器。实现 ToolKit 抽象层 ILoader, 通过 Resources.LoadAsync 加载。
 *                地址即 Resources 下的相对路径 (不含扩展名)。底层资源类型为 UnityEngine.Object。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityToolKit.Runtime.Resource
{
    public sealed class ResourcesLoader : ILoader
    {
        public ELoadType LoadType => ELoadType.Resources;

        public int MaxConcurrentLoads => 0;

        public bool CanLoad(string address)
        {
            return !string.IsNullOrEmpty(address) &&
                   !address.Contains("://") &&
                   !System.IO.Path.IsPathRooted(address);
        }

        public async Task<IAssetHandle> LoadAsync(string address, CancellationToken cancellationToken = default)
        {
            var handle = new AssetHandle(address);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var asset = await LoadResourceAsync(address, cancellationToken).ConfigureAwait(true);
                if (asset == null)
                {
                    handle.SetFailed(ELoadError.NotFound, $"Resources asset not found: {address}");
                    return handle;
                }

                Action unload = null;
                if (!(asset is GameObject) && !(asset is Component))
                {
                    unload = () => Resources.UnloadAsset(asset);
                }
                handle.SetSucceed(asset, unload);
            }
            catch (OperationCanceledException)
            {
                handle.SetCancelled();
            }
            catch (Exception e)
            {
                handle.SetFailed(ELoadError.Unknown, $"Resources load exception: {address}", e);
            }

            return handle;
        }

        private static Task<Object> LoadResourceAsync(string address, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<Object>();
            var request = Resources.LoadAsync<Object>(address);

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
    }
}
