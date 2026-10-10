/*
 * author       : Gordon
 * datetime     : 2026/10/10
 * description  : Unity 资源宿主 = 应用级装配根 (文件缓存与 HTTP 解耦版 §2)。
 *                Awake 创建并持有：应用级 FileCache (远端文件与 AB 加载器共享) +
 *                资源专用 SimpleDownloader (只走 DownloadAsync 入口) + ResourceManager。
 *                退出顺序：停止新资源请求 → 排空资源 → 关闭缓存并等待全部 fill →
 *                释放资源专用下载器 (缓存关闭完成后才 Dispose)。
 *                默认注册 Resources 加载器与 gameObject 工厂；远端文件/AB 加载器用
 *                RegisterRemoteLoader/CreateBundleLoader 接入同一个缓存与下载器实例。
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using ToolKit.Tools.Network;
using UnityEngine;
using ResourceRequest = ToolKit.Tools.Common.ResourceRequest;

namespace UnityToolKit.Runtime.Resource
{
    [DefaultExecutionOrder(-10000)]
    public sealed class UnityResourceHost : MonoBehaviour
    {
        [Tooltip("专用缓存根目录；为空时使用 persistentDataPath/resource-cache")]
        [SerializeField] private string cacheDirectory = "";

        private UnityExecutionContext? _context;
        private ResourceManager? _manager;
        private FileCache? _cache;
        private SimpleDownloader? _downloader;
        private CancellationTokenSource? _lifetime;

        public ResourceManager Resources
        {
            get
            {
                if (_manager == null)
                {
                    throw new InvalidOperationException("UnityResourceHost 尚未完成初始化");
                }
                return _manager;
            }
        }

        public IExecutionContext Context =>
            _context ?? throw new InvalidOperationException("UnityResourceHost 尚未完成初始化");

        /// <summary> 应用级文件缓存：远端文件与 AB 加载器共享的同一实例 </summary>
        public FileCache Cache =>
            _cache ?? throw new InvalidOperationException("UnityResourceHost 尚未完成初始化");

        /// <summary> 资源专用下载器：只走 DownloadAsync 入口，不与业务批量下载队列混用 </summary>
        public SimpleDownloader Downloader =>
            _downloader ?? throw new InvalidOperationException("UnityResourceHost 尚未完成初始化");

        private void Awake()
        {
            _context = new UnityExecutionContext();
            _lifetime = new CancellationTokenSource();

            // 装配根创建缓存与下载器 (§2)：初始化只做启动扫描与清理，不连网
            var directory = string.IsNullOrEmpty(cacheDirectory)
                ? Path.Combine(Application.persistentDataPath, "resource-cache")
                : cacheDirectory;
            _cache = new FileCache(new FileCacheOptions { Directory = directory });
            _ = _cache.InitializeAsync();
            _downloader = new SimpleDownloader(4, new NetworkOptions
            {
                MaxConcurrentDownloads = 4,
            });

            var options = new ResourceSystemOptions
            {
                DefaultLoader = "resources",
            };
            _manager = new ResourceManager(_context, options);
            _manager.RegisterLoader("resources", new ResourcesLoader(_context));
            _manager.RegisterFactory("gameObject", new GameObjectInstanceFactory(_context));
        }

        private void Update()
        {
            _manager?.Tick();
        }

        /// <summary>
        /// 注册远端文件加载器：注入共享缓存与下载方法 (默认 downloader.DownloadAsync)。
        /// 仅配置期允许 (首个请求冻结注册表)。
        /// </summary>
        public void RegisterRemoteLoader(
            DecoderRegistry? decoders = null,
            Func<string, RemoteFileRequest>? requestBuilder = null,
            FileValidity? defaultValidity = null)
        {
            if (_manager == null || _cache == null || _downloader == null)
            {
                throw new InvalidOperationException("UnityResourceHost 尚未完成初始化");
            }
            _manager.RegisterLoader("remote", new RemoteFileLoader(
                _cache, _downloader.DownloadAsync,
                decoders ?? DecoderRegistry.CreateDefault(), requestBuilder, defaultValidity),
                new LoaderPolicy { FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests });
        }

        /// <summary>
        /// 创建远端 AB 加载器：与远端文件加载器共享同一个缓存与下载器实例；
        /// 调用方负责把返回的加载器注册进 ResourceManager (仍处于配置期)。
        /// </summary>
        public SimpleAssetBundleLoader CreateBundleLoader(
            Func<ResourceRequest, Task<BundleLocation>> locator)
        {
            if (_cache == null || _downloader == null || _context == null)
            {
                throw new InvalidOperationException("UnityResourceHost 尚未完成初始化");
            }
            return new SimpleAssetBundleLoader(
                _context, locator, _cache, null, _downloader.DownloadAsync);
        }

        /// <summary>
        /// 停止业务并排空：先由调用方解除 UI 绑定、归还引用与租约，再等待本方法。
        /// 顺序：停止新请求 → 排空资源 → 关闭缓存 (等待全部 fill 收尾) → 释放资源专用下载器。
        /// </summary>
        public async Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            if (_manager == null)
            {
                return;
            }
            _lifetime?.Cancel();
            await _manager.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            if (_cache != null)
            {
                await _cache.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            _downloader?.Dispose(); // 缓存关闭完成后才释放；传输对象归本宿主所有
            _downloader = null;
        }

        /// <summary>
        /// 销毁兜底：按同一顺序启动关闭但不等待 (主线程销毁中无法同步排空)。
        /// 正常关闭必须由持久宿主显式调用 ShutdownAsync 等待完成。
        /// </summary>
        private void OnDestroy()
        {
            _lifetime?.Cancel();
            _manager?.Dispose();
            var cache = _cache;
            var downloader = _downloader;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (cache != null)
                    {
                        await cache.ShutdownAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    downloader?.Dispose(); // 缓存收尾后才释放下载器
                }
            });
            _manager = null;
            _cache = null;
            _downloader = null;
        }
    }
}
