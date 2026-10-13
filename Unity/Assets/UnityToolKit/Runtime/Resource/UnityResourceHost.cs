/*
 * author       : Gordon
 * datetime     : 2026/10/10
 * description  : Unity 资源宿主 = 应用级装配根 (文件缓存与 HTTP 解耦版 §2)。
 *                Awake 创建并持有：应用级 FileCache (远端文件与 AB 加载器共享) +
 *                资源专用 SimpleDownloader (只走 DownloadAsync 入口) + ResourceManager。
 *                项目启动入口 await InitializeAsync：转发管理器的统一初始化，成功后开放 Resources。
 *                Dispose 立即停止使用并触发资源清理；缓存独立取消并等待实际文件 I/O 结束，
 *                随后释放资源专用下载器，不等待业务归还引用或租约。
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
    public sealed class UnityResourceHost : MonoBehaviour, IDisposable
    {
        [Tooltip("专用缓存根目录；为空时使用 persistentDataPath/resource-cache")]
        [SerializeField] private string cacheDirectory = "";

        private UnityExecutionContext? _context;
        private ResourceManager? _manager;
        private FileCache? _cache;
        private FileResourceConverters? _fileConverters;
        private SimpleDownloader? _downloader;
        private int _disposed;

        public ResourceManager Resources
        {
            get
            {
                _ThrowIfDisposed();
                if (_manager == null || !_manager.IsInitialized)
                {
                    throw new InvalidOperationException("请先等待 UnityResourceHost.InitializeAsync 成功完成");
                }
                return _manager;
            }
        }

        public IExecutionContext Context
        {
            get
            {
                _ThrowIfDisposed();
                return _context ?? throw new InvalidOperationException("UnityResourceHost.Awake 尚未完成");
            }
        }

        /// <summary> 初始化成功后取得应用级文件缓存，远端文件与 AB 加载器共享此实例。 </summary>
        public FileCache Cache
        {
            get
            {
                _ThrowIfDisposed();
                return _cache != null && _manager?.IsInitialized == true
                    ? _cache
                    : throw new InvalidOperationException("请先等待 UnityResourceHost.InitializeAsync 成功完成");
            }
        }

        /// <summary> 资源专用下载器：只走 DownloadAsync 入口，不与业务批量下载队列混用 </summary>
        public SimpleDownloader Downloader
        {
            get
            {
                _ThrowIfDisposed();
                return _downloader ?? throw new InvalidOperationException("UnityResourceHost.Awake 尚未完成");
            }
        }

        /// <summary> 文件加载器共用的目标类型转换表；组装时也可注册自定义目标类型。 </summary>
        public FileResourceConverters FileConverters
        {
            get
            {
                _ThrowIfDisposed();
                return _fileConverters ?? throw new InvalidOperationException("UnityResourceHost.Awake 尚未完成");
            }
        }

        private void Awake()
        {
            _context = new UnityExecutionContext();
            _fileConverters = FileResourceConverters.CreateDefault();
            var imageConverter = new UnityImageFileConverter(_context);
            _fileConverters.Register(typeof(Texture2D), imageConverter);
            _fileConverters.Register(typeof(Sprite), imageConverter);

            // Awake 只组装依赖；缓存准备与开放业务入口统一交给 ResourceManager。
            var directory = string.IsNullOrEmpty(cacheDirectory)
                ? Path.Combine(Application.persistentDataPath, "resource-cache")
                : cacheDirectory;
            _cache = new FileCache(new FileCacheOptions { Directory = directory });
            _downloader = new SimpleDownloader(4, new NetworkOptions
            {
                MaxConcurrentDownloads = 4,
            });

            _manager = new ResourceManager(_context, fileCache: _cache);
            _manager.RegisterLoader(ResourceSource.Resources, new ResourcesLoader(_context));
            _manager.RegisterFactory("gameObject", new GameObjectInstanceFactory(_context));
        }

        /// <summary>
        /// 项目启动入口在主线程调用并等待。configure 用于启动前注册或替换默认实现，只执行一次。
        /// 转发管理器的统一初始化，随后 Resources/Cache 才可访问。
        /// 重复调用等待同一次初始化；调用者取消只停止等待，宿主 Dispose 才中止初始化。
        /// </summary>
        public Task InitializeAsync(
            Action<ResourceManager>? configure = null,
            CancellationToken cancellationToken = default)
        {
            _ThrowIfDisposed();
            if (_manager == null)
                throw new InvalidOperationException("UnityResourceHost.Awake 尚未完成");
            return _manager.InitializeAsync(configure, cancellationToken);
        }

        private void Update()
        {
            _manager?.Tick();
        }

        /// <summary>
        /// 注册远端文件加载器：注入共享缓存、转换表与下载方法。
        /// 可在启动前或运行中注册；Remote 来源已注册时会报错。
        /// </summary>
        public void RegisterRemoteLoader(
            Func<string, RemoteFileRequest>? requestBuilder = null,
            FileValidity? defaultValidity = null)
        {
            _ThrowIfDisposed();
            if (_manager == null || _cache == null || _downloader == null)
            {
                throw new InvalidOperationException("UnityResourceHost 尚未完成初始化");
            }
            _manager.RegisterLoader(ResourceSource.Remote, new RemoteFileLoader(
                _cache, _downloader.DownloadAsync,
                FileConverters, requestBuilder, defaultValidity),
                new LoaderPolicy { FailurePolicy = LoadFailurePolicy.ContinueWaitingRequests });
        }

        /// <summary>
        /// 创建远端 AB 加载器：与远端文件加载器共享同一个缓存与下载器实例；
        /// 调用方在 InitializeAsync 的 configure 中或运行中通过 Resources.RegisterLoader 注册，
        /// 业务随后按 AssetBundle 来源加载，无需知道加载器名称。
        /// </summary>
        public SimpleAssetBundleLoader CreateBundleLoader(
            Func<ResourceRequest, Task<BundleLocation>> locator)
        {
            _ThrowIfDisposed();
            if (_cache == null || _downloader == null || _context == null)
            {
                throw new InvalidOperationException("UnityResourceHost 尚未完成初始化");
            }
            return new SimpleAssetBundleLoader(
                _context, locator, _cache, null, _downloader.DownloadAsync);
        }

        /// <summary>
        /// 立即停止使用，交由管理器作废引用与租约并清理资源；重复调用无副作用。
        /// 文件 I/O 独立取消并收尾，结束后释放下载器；本方法不阻塞主线程。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            var cache = _cache;
            var downloader = _downloader;
            try
            {
                _manager?.Dispose();
            }
            finally
            {
                _manager = null;
                _cache = null;
                _downloader = null;
                _ = _DisposeFilesAsync(cache, downloader);
            }
        }

        private static async Task _DisposeFilesAsync(FileCache? cache, SimpleDownloader? downloader)
        {
            try
            {
                try
                {
                    if (cache != null)
                        await cache.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    downloader?.Dispose(); // 全部 fill 已实际结束，才可释放传输对象。
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private void _ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(UnityResourceHost));
        }

        private void OnApplicationQuit() => Dispose();

        private void OnDestroy() => Dispose();
    }
}
