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

namespace UnityToolKit.Engine.ResourceSystem
{
    public sealed class ResourcesLoader : ILoader
    {
        public ELoadType LoadType => ELoadType.Resources;

        // Resources 地址没有协议前缀, 也不是绝对路径; Auto 路由时作为兜底加载器置于末位即可。
        public bool CanLoad(string address)
        {
            return !string.IsNullOrEmpty(address) &&
                   !address.Contains("://") &&
                   !System.IO.Path.IsPathRooted(address);
        }

        public async Task<IAssetHandle> LoadAsync(
            string address,
            CancellationToken cancellationToken = default)
        {
            var handle = new AssetHandle(address);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var asset = await _LoadResourceAsync(address, cancellationToken).ConfigureAwait(true);
                if (asset == null)
                {
                    handle.SetFailed(new Exception($"Resources 中找不到资源: {address}"));
                    return handle;
                }

                // 卸载: 非 GameObject/Component 资源可用 Resources.UnloadAsset 精确卸载;
                //       GameObject 预制体只能依赖 Resources.UnloadUnusedAssets, 这里不做强卸载。
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
                handle.SetFailed(e);
            }

            return handle;
        }

        private static Task<Object> _LoadResourceAsync(
            string address, CancellationToken cancellationToken)
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
