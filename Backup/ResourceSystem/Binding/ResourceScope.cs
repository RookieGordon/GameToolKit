/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 资源作用域 (P6, §15.2)。Own(IDisposable)/Own(Action) 按注册的逆序执行清理，
 *                逐项收集异常，不因一个清理失败跳过其余项。对于界面应先注册资源归还，
 *                再注册解除展示的回调，使关闭时先清展示。不提供新的资源获取机制。
 */

using System;
using System.Collections.Generic;

namespace ToolKit.Tools.Common
{
    public sealed class ResourceScope : IDisposable
    {
        private readonly object _gate = new object();
        private readonly List<Action> _cleanups = new List<Action>();
        private bool _disposed;

        /// <summary> 登记一项清理 (资源归还或解除展示的回调)；作用域释放时逆序执行 </summary>
        public void Own(IDisposable disposable)
        {
            if (disposable == null) throw new ArgumentNullException(nameof(disposable));
            Own(new Action(disposable.Dispose));
        }

        public void Own(Action cleanup)
        {
            if (cleanup == null) throw new ArgumentNullException(nameof(cleanup));
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(ResourceScope), "作用域已释放，不能再登记清理项");
                }
                _cleanups.Add(cleanup);
            }
        }

        public bool IsDisposed
        {
            get { lock (_gate) return _disposed; }
        }

        /// <summary> 逆序执行全部清理；逐项收集异常，最后以 AggregateException 抛出 </summary>
        public void Dispose()
        {
            List<Exception>? errors = null;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
            }
            for (var i = _cleanups.Count - 1; i >= 0; i--)
            {
                try
                {
                    _cleanups[i]();
                }
                catch (Exception ex)
                {
                    errors ??= new List<Exception>();
                    errors.Add(ex);
                }
            }
            _cleanups.Clear();
            if (errors != null)
            {
                throw new AggregateException("作用域清理存在失败项", errors);
            }
        }
    }
}
