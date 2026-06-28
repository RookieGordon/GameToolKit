/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : IAssetHandle 的通用实现 (引擎无关)。
 *                - 持有底层资源对象与"卸载委托"(由具体 Loader 注入, 不同来源卸载方式不同)。
 *                - 维护引用计数; 计数归零时不直接卸载, 而是通过 OnReachedZero 通知 ResourceManager,
 *                  由 Manager 决定立即卸载还是延迟卸载(归零后保留一段时间, 命中可复活)。
 *                - 真正的资源卸载由 Manager 在合适时机调用 Unload() 执行。
 *                所有 Loader 都应当复用本类, 无需各自实现引用计数逻辑。
 */

using System;

namespace ToolKit.Tools.Common
{
    public sealed class AssetHandle : IAssetHandle
    {
        private readonly object _lock = new object();

        private object _asset;
        private int _refCount;
        private Action _unloadAction;

        public string Address { get; }
        public ELoadStatus Status { get; private set; }
        public int ReferenceCount => _refCount;
        public bool IsSuccess => Status == ELoadStatus.Succeed;
        public LoadError Error { get; private set; }

        /// <summary>
        /// 引用计数归零时回调 (由 ResourceManager 注入)。Manager 据此决定立即卸载或延迟卸载。
        /// 注意: 此时资源尚未卸载, Status 仍为 Succeed, 可被再次 Retain 复活。
        /// </summary>
        public Action<AssetHandle> OnReachedZero { get; set; }

        public AssetHandle(string address)
        {
            Address = address;
            Status = ELoadStatus.Loading;
        }

        #region 由 Loader 调用的状态设置

        /// <summary>
        /// 标记加载成功。
        /// </summary>
        /// <param name="asset">底层资源对象</param>
        /// <param name="unloadAction">卸载该资源的委托 (如 Resources.UnloadAsset / ab.Unload / 置空 byte[])</param>
        public void SetSucceed(object asset, Action unloadAction)
        {
            lock (_lock)
            {
                _asset = asset;
                _unloadAction = unloadAction;
                Status = ELoadStatus.Succeed;
            }
        }

        /// <summary> 标记加载失败 (结构化错误: 码 + 可读信息 + 可选原始异常) </summary>
        public void SetFailed(ELoadError code, string message, Exception inner = null)
        {
            lock (_lock)
            {
                Error = new LoadError(code, message, inner);
                Status = ELoadStatus.Failed;
            }
            Log.Error($"[ResourceSystem] 加载失败: {Address} -> [{code}] {message}");
        }

        /// <summary> 标记加载被取消 </summary>
        public void SetCancelled()
        {
            lock (_lock)
            {
                Error = new LoadError(ELoadError.Cancelled, $"加载已取消: {Address}");
                Status = ELoadStatus.Cancelled;
            }
        }

        #endregion

        public T GetAsset<T>() where T : class
        {
            return _asset as T;
        }

        /// <inheritdoc/>
        public void Retain()
        {
            lock (_lock)
            {
                if (Status == ELoadStatus.Unloaded)
                {
                    Log.Error($"[ResourceSystem] 句柄已卸载, 不可再 Retain: {Address}");
                    return;
                }
                _refCount++;
            }
        }

        /// <inheritdoc/>
        public void Release()
        {
            bool reachedZero;
            lock (_lock)
            {
                if (_refCount <= 0)
                {
                    Log.Error($"[ResourceSystem] 引用计数已为 0, Release 调用过多: {Address}");
                    return;
                }

                _refCount--;
                reachedZero = _refCount == 0;
            }

            // 引用归零: 通知 Manager 由其决定卸载时机 (立即 / 延迟)。这里不直接卸载。
            if (reachedZero)
            {
                OnReachedZero?.Invoke(this);
            }
        }

        /// <summary>
        /// 真正卸载底层资源 (由 ResourceManager 调用)。幂等。
        /// </summary>
        public void Unload()
        {
            Action unload;
            lock (_lock)
            {
                if (Status == ELoadStatus.Unloaded)
                {
                    return;
                }
                unload = _unloadAction;
                _unloadAction = null;
                _asset = null;
                Status = ELoadStatus.Unloaded;
            }
            unload?.Invoke();
        }
    }
}
