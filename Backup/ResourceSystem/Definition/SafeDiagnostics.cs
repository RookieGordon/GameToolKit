/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 诊断安全分发器 (R24)。可替换的 IResourceDiagnostics 实现抛出的异常
 *                不得中断主生命周期：先提交所有状态与终局任务，再做诊断通知；
 *                观察者故障被隔离且不重入原故障链 (§11.4/§11.5)。
 */

using System;

namespace ToolKit.Tools.Common
{
    /// <summary> 包装任意诊断实现：Report/Trace 永不抛出 </summary>
    public sealed class SafeResourceDiagnostics : IResourceDiagnostics
    {
        private readonly IResourceDiagnostics _inner;

        public SafeResourceDiagnostics(IResourceDiagnostics inner)
        {
            _inner = inner ?? NullResourceDiagnostics.Instance;
        }

        public void Report(LoadError error)
        {
            try
            {
                _inner.Report(error);
            }
            catch (Exception)
            {
                // 观察者故障不得影响资源所有权与清理流程
            }
        }

        public void Trace(ResourceEvent traceEvent)
        {
            try
            {
                _inner.Trace(traceEvent);
            }
            catch (Exception)
            {
            }
        }
    }
}
