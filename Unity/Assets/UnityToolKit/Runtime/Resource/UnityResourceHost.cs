/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : Unity 资源宿主 (P5, §12.2/§7.5)。持有 ResourceManager 与主线程执行上下文，
 *                Update 驱动 Tick 维护；OnDestroy 启动/推进系统关闭 (销毁兜底不可作为
 *                正常关闭唯一入口，推荐由持久宿主显式等待 ShutdownAsync)。
 *                默认装配 Resources 加载器；装配根可按需注册 Bundle/本地/远端加载器后
 *                再发起首个请求 (首次请求冻结注册表)。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;

namespace UnityToolKit.Runtime.Resource
{
    [DefaultExecutionOrder(-10000)]
    public sealed class UnityResourceHost : MonoBehaviour
    {
        private UnityExecutionContext? _context;
        private ResourceManager? _manager;
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

        private void Awake()
        {
            _context = new UnityExecutionContext();
            _lifetime = new CancellationTokenSource();
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

        /// <summary> 停止业务并排空：先由调用方解除 UI 绑定、归还引用与租约，再等待本方法 </summary>
        public async Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            if (_manager == null)
            {
                return;
            }
            _lifetime?.Cancel();
            await _manager.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            // 本宿主独占的文件缓存、下载组件由装配根在此之后关闭，最后停止执行上下文
        }

        /// <summary>
        /// 销毁兜底：启动关闭但不等待 (主线程销毁中无法同步排空)。
        /// 正常关闭必须由持久宿主显式调用 ShutdownAsync 等待完成。
        /// </summary>
        private void OnDestroy()
        {
            _lifetime?.Cancel();
            _manager?.Dispose();
            _manager = null;
        }
    }
}
