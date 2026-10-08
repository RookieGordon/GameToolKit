/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 极简 AssetBundle 加载器 (P5, §8.3)。接收用户配置的 locator(ResourceRequest)→BundleLocation；
 *                旧 "bundlePath::assetName" 可作为默认解析器。BundleLocation 属本实现私有协议。
 *                容器级共享加载/等待者/引用计数/卸载屏障遵守第 6 节模式 (v1 以共享任务 + 同键屏障实现)；
 *                依赖环被检测并拒绝；默认 Unload(true) 卸载已加载对象 —— 业务从预制体自行实例化时
 *                必须在实例存活期间持有对应引用，推荐使用框架 RentAsync。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common.Resource;
using UnityEngine;
using Object = UnityEngine.Object;
using ResourceRequest = ToolKit.Tools.Common.Resource.ResourceRequest;

namespace UnityToolKit.Runtime.Resource
{
    /// <summary> 内置加载器的私有地址契约：容器身份/版本、本地路径或远端请求、包内名称与可选依赖 </summary>
    public sealed class BundleLocation
    {
        public string ContainerId = "";
        public string Version = "";
        public string LocalPath = "";
        public FileRequest? RemoteRequest;
        public string AssetName = "";
        public List<BundleLocation> Dependencies = new List<BundleLocation>();
    }

    /// <summary> 一次容器持有 (容器 + 依赖租约 + 文件租约) </summary>
    internal sealed class BundleLease
    {
        public BundleEntry Entry;
        public bool Released;

        public BundleLease(BundleEntry entry)
        {
            Entry = entry;
        }
    }

    internal sealed class BundleEntry
    {
        public string Identity = "";
        public AssetBundle? Bundle;
        public int Holds;
        public bool Unloading;
        public Task<AssetBundle?>? Loading; // 共享加载任务
        public readonly List<BundleLease> Dependencies = new List<BundleLease>();
        public FileLease? File;
    }

    public sealed class SimpleAssetBundleLoader : IResourceLoader
    {
        public const string Separator = "::";

        private readonly IExecutionContext _context;
        private readonly FileCache? _fileCache; // 远端容器经 FileCache 获取本地文件
        private readonly Func<ResourceRequest, Task<BundleLocation>> _locator;
        private readonly object _gate = new object();
        private readonly Dictionary<string, BundleEntry> _entries = new Dictionary<string, BundleEntry>();

        /// <param name="locator">地址 → BundleLocation；返回 null 视为未找到容器</param>
        public SimpleAssetBundleLoader(
            IExecutionContext context,
            Func<ResourceRequest, Task<BundleLocation>> locator,
            FileCache? fileCache = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _locator = locator ?? throw new ArgumentNullException(nameof(locator));
            _fileCache = fileCache;
        }

        /// <summary> 默认解析器：旧 "bundlePath::assetName" 格式，无依赖、无版本 </summary>
        public static Task<BundleLocation> DefaultLocator(ResourceRequest request)
        {
            var idx = request.Address.IndexOf(Separator, StringComparison.Ordinal);
            if (idx <= 0 || idx + Separator.Length >= request.Address.Length)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "address", request.Address },
                        { "reason", $"应为 bundlePath{Separator}assetName" },
                    }));
            }
            var path = request.Address.Substring(0, idx);
            var assetName = request.Address.Substring(idx + Separator.Length);
            return Task.FromResult(new BundleLocation
            {
                ContainerId = path,
                Version = "",
                LocalPath = path,
                AssetName = assetName,
            });
        }

        public async Task<ResolvedResource> ResolveAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            var location = await _locator(request).ConfigureAwait(false);
            if (location == null || string.IsNullOrEmpty(location.ContainerId)
                || string.IsNullOrEmpty(location.AssetName))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LoaderResolveFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "address", request.Address } }));
            }
            var localKey = string.Join("|",
                "bundle", location.ContainerId, location.Version, location.AssetName,
                request.RequestedType.FullName ?? request.RequestedType.Name);
            return new ResolvedResource(localKey, request.RequestedType, location);
        }

        public async Task<LoadedAsset> LoadAsync(
            ResolvedResource resource, IProgress<ResourceProgress> progress, CancellationToken operationToken)
        {
            var location = (BundleLocation)resource.Payload!;
            var rootLease = await AcquireBundleGraphAsync(location, operationToken, new HashSet<string>())
                .ConfigureAwait(false);
            try
            {
                var bundle = rootLease.Entry.Bundle!;
                var asset = await _context.InvokeAsync(() =>
                        _LoadAssetOnMainThread(bundle, location.AssetName, resource.RepresentationType, operationToken))
                    .ConfigureAwait(false);
                if (asset == null)
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.AssetNotFound, LoadStage.LoadAsset, CleanupStatus.Complete, null,
                        new Dictionary<string, object>
                        {
                            { "container", location.ContainerId },
                            { "asset", location.AssetName },
                        }));
                }
                return new LoadedAsset(asset, null,
                    isAlive: () => asset != null,
                    releaseAsync: () => ReleaseBundleAsync(rootLease));
            }
            catch
            {
                await ReleaseBundleAsync(rootLease).ConfigureAwait(false); // 回退本次取得的容器持有
                throw;
            }
        }

        #region 容器图获取与释放 (§8.3)

        /// <summary> 获取容器图：容器自身 + 递归依赖；检测并拒绝依赖环 </summary>
        internal async Task<BundleLease> AcquireBundleGraphAsync(
            BundleLocation location, CancellationToken ct, HashSet<string> visitedIdentities)
        {
            var identity = _EncodeIdentity(location.ContainerId, location.Version);
            if (!visitedIdentities.Add(identity))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetLoadFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "container", location.ContainerId },
                        { "reason", "bundle.dependency_cycle" },
                    }));
            }
            try
            {
                return await _OpenContainerAsync(location, identity, ct, visitedIdentities).ConfigureAwait(false);
            }
            finally
            {
                visitedIdentities.Remove(identity);
            }
        }

        private async Task<BundleLease> _OpenContainerAsync(
            BundleLocation location, string identity, CancellationToken ct, HashSet<string> visited)
        {
            while (true)
            {
                Task<AssetBundle?>? sharedLoad;
                lock (_gate)
                {
                    if (_entries.TryGetValue(identity, out var entry))
                    {
                        if (entry.Unloading)
                        {
                            // 卸载屏障：等待旧代结束后重新解析 (v1 以自旋让步实现，同键不并发开包)
                            sharedLoad = null;
                        }
                        else if (entry.Bundle != null)
                        {
                            entry.Holds++; // 已加载：增加容器持有并交付
                            return new BundleLease(entry);
                        }
                        else
                        {
                            sharedLoad = entry.Loading; // 加入共享加载
                        }
                    }
                    else
                    {
                        var fresh = new BundleEntry { Identity = identity };
                        _entries[identity] = fresh;
                        fresh.Loading = _LoadBundleEntryAsync(fresh, location, ct, visited);
                        sharedLoad = fresh.Loading;
                    }
                }

                if (sharedLoad == null)
                {
                    await Task.Yield(); // 卸载屏障：稍后重试同键
                    continue;
                }

                await sharedLoad.WaitWithCancellation(ct).ConfigureAwait(false); // 取消只中断等待，不取消共享加载
                ct.ThrowIfCancellationRequested();
                var bundle = sharedLoad.Result; // 到达此处共享任务已成功完成
                lock (_gate)
                {
                    if (bundle != null && _entries.TryGetValue(identity, out var entry2)
                        && ReferenceEquals(entry2.Bundle, bundle))
                    {
                        entry2.Holds++; // 成功后登记持有，再返回调用者
                        return new BundleLease(entry2);
                    }
                }
                if (bundle == null)
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.AssetNotFound, LoadStage.LoadAsset, CleanupStatus.Complete, null,
                        new Dictionary<string, object> { { "container", location.ContainerId } }));
                }
                // 共享任务成功但条目已被替换：重新走获取路径
            }
        }

        private async Task<AssetBundle?> _LoadBundleEntryAsync(
            BundleEntry entry, BundleLocation location, CancellationToken ct, HashSet<string> visited)
        {
            var dependencies = new List<BundleLease>();
            FileLease? fileLease = null;
            try
            {
                // 依赖先于本包加载；失败时反向释放
                foreach (var dependency in location.Dependencies)
                {
                    dependencies.Add(await AcquireBundleGraphAsync(dependency, ct, visited).ConfigureAwait(false));
                }

                string path = location.LocalPath;
                if (location.RemoteRequest != null)
                {
                    if (_fileCache == null)
                    {
                        throw new ResourceLoadException(new LoadError(
                            DiagnosticCodes.AssetLoadFailed, LoadStage.Resolve, CleanupStatus.Complete, null,
                            new Dictionary<string, object>
                            {
                                { "container", location.ContainerId },
                                { "reason", "remote-container-needs-file-cache" },
                            }));
                    }
                    fileLease = await _fileCache.AcquireAsync(location.RemoteRequest, ct).ConfigureAwait(false);
                    path = fileLease.Path;
                }

                var bundle = await _context.InvokeAsync(() => _LoadBundleOnMainThread(path, ct))
                    .ConfigureAwait(false);
                if (bundle == null)
                {
                    throw new ResourceLoadException(new LoadError(
                        DiagnosticCodes.AssetNotFound, LoadStage.LoadAsset, CleanupStatus.Complete, null,
                        new Dictionary<string, object> { { "container", location.ContainerId }, { "path", path } }));
                }

                lock (_gate)
                {
                    entry.Bundle = bundle;
                    entry.File = fileLease;
                    entry.Dependencies.AddRange(dependencies);
                    entry.Loading = null;
                    dependencies = new List<BundleLease>(); // 所有权已转交条目
                }
                return bundle;
            }
            catch
            {
                // 失败回退：释放已取得的依赖与文件；保留原始失败
                foreach (var dependency in dependencies)
                {
                    await ReleaseBundleAsync(dependency).ConfigureAwait(false);
                }
                fileLease?.Dispose();
                lock (_gate)
                {
                    entry.Loading = null;
                    if (_entries.TryGetValue(entry.Identity, out var current) && ReferenceEquals(current, entry))
                    {
                        _entries.Remove(entry.Identity); // 失败不留可命中项
                    }
                }
                return null; // 共享任务以 null 收尾：等待者按容器加载失败处理
            }
        }

        /// <summary> 归还一次容器持有；归零后 Unload(true) → 依赖 → 文件租约 </summary>
        internal async Task ReleaseBundleAsync(BundleLease lease)
        {
            AssetBundle? toUnload;
            List<BundleLease> dependencies;
            FileLease? fileLease;
            lock (_gate)
            {
                if (lease.Released)
                {
                    return;
                }
                lease.Released = true;
                var entry = lease.Entry;
                if (entry.Holds > 0)
                {
                    entry.Holds--;
                }
                if (entry.Holds != 0 || entry.Unloading)
                {
                    return;
                }
                entry.Unloading = true; // 卸载屏障：阻止同容器身份新开包
                toUnload = entry.Bundle;
                dependencies = new List<BundleLease>(entry.Dependencies);
                entry.Dependencies.Clear();
                fileLease = entry.File;
                entry.File = null;
                entry.Bundle = null;
                if (_entries.TryGetValue(entry.Identity, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(entry.Identity);
                }
            }

            if (toUnload != null)
            {
                // 默认卸载已加载对象：包的资产持有、池原型与其他包依赖持有都已结束为前提
                _context.Invoke(() => toUnload!.Unload(true));
            }
            foreach (var dependency in dependencies)
            {
                await ReleaseBundleAsync(dependency).ConfigureAwait(false);
            }
            fileLease?.Dispose(); // 最后释放仍需保护的文件租约
        }

        private static string _EncodeIdentity(string containerId, string version)
        {
            return containerId + "@" + version;
        }

        #endregion

        #region 主线程操作

        private static async Task<AssetBundle?> _LoadBundleOnMainThread(string path, CancellationToken ct)
        {
            var request = AssetBundle.LoadFromFileAsync(path);
            var tcs = new TaskCompletionSource<AssetBundle?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<AsyncOperation>? handler = null;
            var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            handler = _ =>
            {
                request.completed -= handler;
                tcs.TrySetResult(request.assetBundle);
            };
            request.completed += handler;
            var bundle = await tcs.Task.ConfigureAwait(false);
            registration.Dispose();
            return bundle;
        }

        private static async Task<Object> _LoadAssetOnMainThread(
            AssetBundle bundle, string assetName, Type type, CancellationToken ct)
        {
            var request = bundle.LoadAssetAsync(assetName, type);
            var tcs = new TaskCompletionSource<Object>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<AsyncOperation>? handler = null;
            var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            handler = _ =>
            {
                request.completed -= handler;
                tcs.TrySetResult(request.asset);
            };
            request.completed += handler;
            var asset = await tcs.Task.ConfigureAwait(false);
            registration.Dispose();
            return asset!;
        }

        #endregion
    }
}
