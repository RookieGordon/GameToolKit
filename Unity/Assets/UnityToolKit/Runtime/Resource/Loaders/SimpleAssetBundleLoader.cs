/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 极简 AssetBundle 加载器 (P5, §8.3)。接收用户配置的 locator(ResourceRequest)→BundleLocation；
 *                旧 "bundlePath::assetName" 可作为默认解析器。BundleLocation 属本实现私有协议。
 *                R02：容器共享加载使用容器自己的操作令牌与等待者计数 —— 单个资源请求的取消只移除
 *                自己的等待，最后一个等待者退出才取消容器操作；零接收者的成功结果进入释放。
 *                R03：Unity 不可中断请求观察到真实完成，取消后的迟到包立即回收。
 *                R04：卸载形成同容器屏障 —— Unloading 条目保留到 Unload/依赖/文件回退完成，
 *                释放失败保留隔离状态与所有权记录；新请求等待 Terminal 而非自旋。
 *                R17：失败保留原始 ResourceLoadException；回退失败以 RelatedErrors 叠加，不覆盖主错误。
 *                默认 Unload(true)：业务从预制体自行实例化时必须在实例存活期间持有对应引用。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;
using Object = UnityEngine.Object;
using ResourceRequest = ToolKit.Tools.Common.ResourceRequest;

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
        public int Waiters; // 正在等待共享任务判定的请求数 (R02)
        public bool Unloading;
        public Task<AssetBundle?>? Loading;
        public readonly List<BundleLease> Dependencies = new List<BundleLease>();
        /// <summary> 容器自己的操作令牌：与任何业务调用者的取消令牌无关 (R02) </summary>
        public readonly CancellationTokenSource OperationCts = new CancellationTokenSource();
        public readonly TaskCompletionSource<object> Terminal =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        public LoadError? StoredFault;
    }

    public sealed class SimpleAssetBundleLoader : IResourceLoader
    {
        public const string Separator = "::";

        private readonly IExecutionContext _context;
        private readonly FileCache? _fileCache;
        private readonly Func<ResourceRequest, Task<BundleLocation>> _locator;
        private readonly IResourceDiagnostics _diagnostics;
        private readonly object _gate = new object();
        private readonly Dictionary<string, BundleEntry> _entries = new Dictionary<string, BundleEntry>();

        /// <param name="locator">地址 → BundleLocation；返回 null 视为未找到容器</param>
        public SimpleAssetBundleLoader(
            IExecutionContext context,
            Func<ResourceRequest, Task<BundleLocation>> locator,
            FileCache? fileCache = null,
            IResourceDiagnostics? diagnostics = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _locator = locator ?? throw new ArgumentNullException(nameof(locator));
            _fileCache = fileCache;
            _diagnostics = new SafeResourceDiagnostics(diagnostics ?? NullResourceDiagnostics.Instance);
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
                // Unity 资产请求观察到真实完成；取消时先等资产请求结束再归还容器 (R03)
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
            catch (Exception ex)
            {
                // 回退本次取得的容器持有；回退失败不覆盖原始错误 (R17)
                LoadError error = ex is ResourceLoadException rle
                    ? rle.Error
                    : new LoadError(DiagnosticCodes.AssetLoadFailed, LoadStage.LoadAsset,
                        CleanupStatus.Unknown, ex, new Dictionary<string, object>
                        {
                            { "container", location.ContainerId },
                            { "asset", location.AssetName },
                        });
                try
                {
                    await ReleaseBundleAsync(rootLease).ConfigureAwait(false);
                }
                catch (Exception rollbackEx)
                {
                    error = error.WithRelated(new LoadError(
                        DiagnosticCodes.LifecycleReleaseFailed, LoadStage.ReleaseAsset,
                        CleanupStatus.Incomplete, rollbackEx));
                }
                if (ReferenceEquals(error, ex is ResourceLoadException same ? same.Error : null))
                {
                    throw; // 原始异常原样抛出，保留堆栈
                }
                throw new ResourceLoadException(error);
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
                    DiagnosticCodes.BundleDependencyCycle, LoadStage.Resolve, CleanupStatus.Complete, null,
                    new Dictionary<string, object>
                    {
                        { "container", location.ContainerId },
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
                Task<AssetBundle?>? shared = null;
                Task? barrier = null;
                BundleEntry? waiterEntry = null;
                lock (_gate)
                {
                    if (_entries.TryGetValue(identity, out var entry))
                    {
                        if (entry.Unloading)
                        {
                            // 卸载屏障 (R04)：等待旧代终局 (含回退故障) 后重试，不并发开包
                            barrier = entry.Terminal.Task;
                        }
                        else if (entry.Bundle != null)
                        {
                            entry.Holds++; // 已加载：增加容器持有并交付
                            return new BundleLease(entry);
                        }
                        else if (entry.Loading != null)
                        {
                            waiterEntry = entry;
                            entry.Waiters++;
                            shared = entry.Loading;
                        }
                        else
                        {
                            // 上一个条目已被移除的窗口：重建
                            waiterEntry = entry;
                            entry.Waiters++;
                            entry.Loading = _LoadBundleEntryAsync(entry, location, visited);
                            shared = entry.Loading;
                        }
                    }
                    else
                    {
                        waiterEntry = new BundleEntry { Identity = identity };
                        _entries[identity] = waiterEntry;
                        waiterEntry.Waiters = 1;
                        waiterEntry.Loading = _LoadBundleEntryAsync(waiterEntry, location, visited);
                        shared = waiterEntry.Loading;
                    }
                }

                if (barrier != null)
                {
                    await barrier.WaitWithCancellation(ct).ConfigureAwait(false);
                    continue;
                }

                BundleLease? lease = null;
                try
                {
                    await shared!.WaitWithCancellation(ct).ConfigureAwait(false); // 只取消本请求的等待
                    ct.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        if (_entries.TryGetValue(identity, out var current)
                            && ReferenceEquals(current, waiterEntry)
                            && current.Bundle != null && !current.Unloading)
                        {
                            current.Holds++; // 成功后登记容器持有，再返回调用者
                            lease = new BundleLease(current);
                        }
                    }
                }
                finally
                {
                    // 等待者退出：最后一个退出时取消被放弃的共享操作，或回收零持有结果 (R02)
                    _OnWaiterLeft(identity);
                }
                if (lease == null)
                {
                    continue; // 条目在等待期间被移除/进入卸载：重新解析
                }
                return lease;
            }
        }

        /// <summary> 等待者退出记账；共享结果无人接收或共享操作无人等待时回收/取消 </summary>
        private void _OnWaiterLeft(string identity)
        {
            bool cancelOperation = false;
            bool releaseUnheld = false;
            lock (_gate)
            {
                if (!_entries.TryGetValue(identity, out var entry) || entry.Waiters <= 0)
                {
                    return;
                }
                entry.Waiters--;
                if (entry.Waiters != 0)
                {
                    return;
                }
                if (entry.Loading != null)
                {
                    cancelOperation = true; // 最后等待者退出：取消被放弃的容器下载/打开
                }
                else if (entry.Bundle != null && entry.Holds == 0 && !entry.Unloading)
                {
                    releaseUnheld = true; // 成功但无人接收：进入释放
                }
            }
            if (cancelOperation)
            {
                _CancelEntryQuietly(identity);
            }
            else if (releaseUnheld)
            {
                _ = _ReleaseIfUnheldAsync(identity);
            }
        }

        private void _CancelEntryQuietly(string identity)
        {
            try
            {
                lock (_gate)
                {
                    if (_entries.TryGetValue(identity, out var entry) && entry.Loading != null)
                    {
                        entry.OperationCts.Cancel();
                    }
                }
            }
            catch (Exception)
            {
                // CTS 已释放等情况
            }
        }

        private async Task _ReleaseIfUnheldAsync(string identity)
        {
            BundleLease? lease = null;
            lock (_gate)
            {
                if (_entries.TryGetValue(identity, out var entry)
                    && entry.Bundle != null && entry.Holds == 0 && !entry.Unloading)
                {
                    lease = new BundleLease(entry);
                }
            }
            if (lease != null)
            {
                await ReleaseBundleAsync(lease).ConfigureAwait(false);
            }
        }

        private async Task<AssetBundle?> _LoadBundleEntryAsync(
            BundleEntry entry, BundleLocation location, HashSet<string> visited)
        {
            // 容器自己的操作令牌：不受任何业务调用者取消令牌控制 (R02)
            var ct = entry.OperationCts.Token;
            var dependencies = new List<BundleLease>();
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
                    // 缓存路径在本次运行内不会被删除或覆盖：无须文件租约
                    path = await _fileCache.GetFileAsync(location.RemoteRequest, ct).ConfigureAwait(false);
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
                    entry.Dependencies.AddRange(dependencies);
                    dependencies = new List<BundleLease>(); // 所有权已转交条目
                    entry.Loading = null;
                }
                return bundle;
            }
            catch
            {
                // 回退已取得的依赖与文件；回退失败不覆盖原始错误 (R17)
                foreach (var dependency in dependencies)
                {
                    try
                    {
                        await ReleaseBundleAsync(dependency).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // 原始失败优先；回退残留由对应条目的 Terminal 故障暴露
                    }
                }
                lock (_gate)
                {
                    entry.Loading = null;
                    if (_entries.TryGetValue(entry.Identity, out var current) && ReferenceEquals(current, entry))
                    {
                        _entries.Remove(entry.Identity); // 失败不留可命中项
                    }
                }
                throw; // 保留原始失败：DNS/HTTP/磁盘/依赖环等证据不被吞成 not_found (R17)
            }
        }

        /// <summary>
        /// 归还一次容器持有；归零后形成同容器卸载屏障 (R04)：条目保留到 Unload → 依赖 → 文件
        /// 全部回退完成才移除并完成 Terminal；回退失败保留隔离状态与所有权记录。
        /// </summary>
        internal async Task ReleaseBundleAsync(BundleLease lease)
        {
            AssetBundle? toUnload;
            List<BundleLease> dependencies;
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
                entry.Unloading = true; // 卸载屏障：新请求等待 Terminal
                toUnload = entry.Bundle;
                dependencies = new List<BundleLease>(entry.Dependencies);
                entry.Dependencies.Clear();
                entry.Bundle = null;
                entry.Loading = null;
            }

            LoadError? fault = null;
            if (toUnload != null)
            {
                try
                {
                    // 默认卸载已加载对象：包的资产持有、池原型与其他包依赖持有都已结束为前提
                    _context.Invoke(() => toUnload.Unload(true));
                }
                catch (Exception ex)
                {
                    fault = _ReleaseFault("bundle-unload", ex);
                }
            }
            foreach (var dependency in dependencies)
            {
                try
                {
                    await ReleaseBundleAsync(dependency).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    fault ??= _ReleaseFault("dependency-release", ex);
                }
            }
            lock (_gate)
            {
                var entry = lease.Entry;
                if (_entries.TryGetValue(entry.Identity, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(entry.Identity);
                }
                entry.OperationCts.Dispose();
                if (fault != null)
                {
                    // 释放回退失败：保留隔离状态，Terminal 以故障结束，同容器不开新包 (R04)
                    entry.StoredFault = fault;
                    entry.Terminal.TrySetException(new ResourceLoadException(fault));
                }
                else
                {
                    entry.Terminal.TrySetResult(null!);
                }
            }
            if (fault != null)
            {
                _diagnostics.Report(fault);
            }
        }

        private static LoadError _ReleaseFault(string phase, Exception ex)
        {
            return new LoadError(
                DiagnosticCodes.LifecycleReleaseFailed, LoadStage.ReleaseAsset,
                CleanupStatus.Incomplete, ex,
                new Dictionary<string, object> { { "phase", phase } });
        }

        private static string _EncodeIdentity(string containerId, string version)
        {
            return containerId + "@" + version;
        }

        #endregion

        #region 主线程操作

        private static Task<AssetBundle?> _LoadBundleOnMainThread(string path, CancellationToken ct)
        {
            var request = AssetBundle.LoadFromFileAsync(path);
            return UnityAsyncOperationAwaiter.ObserveAsync<AssetBundle>(
                request, () => request.assetBundle,
                disposeLate: late =>
                {
                    // 取消后的迟到包：立即回收，不允许失去所有者 (R03)
                    if (late != null)
                    {
                        late.Unload(true);
                    }
                }, ct);
        }

        private static Task<Object> _LoadAssetOnMainThread(
            AssetBundle bundle, string assetName, Type type, CancellationToken ct)
        {
            return _ObserveAssetAsync(bundle, assetName, type, ct)!;
        }

        private static async Task<Object?> _ObserveAssetAsync(
            AssetBundle bundle, string assetName, Type type, CancellationToken ct)
        {
            var request = bundle.LoadAssetAsync(assetName, type);
            // 包内资产的迟到结果随容器租约释放 (Unload(true)) 回收，无需单独处置
            return await UnityAsyncOperationAwaiter.ObserveAsync<Object>(
                request, () => request.asset, disposeLate: null, ct).ConfigureAwait(false);
        }

        #endregion
    }
}
