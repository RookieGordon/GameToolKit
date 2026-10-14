/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 实例租约：只持有和归还一个实例，不调度创建或销毁。
 *                归还始终投递回状态串行域 —— 归还可能来自实例销毁回调，
 *                同上下文立即归还等于把状态修改嵌进引擎回调。
 */

using System;
using System.Collections.Generic;
using System.Threading;

namespace ToolKit.Tools.Common
{
    /// <summary> 一次实例租用：`Value` 会做存活检查，只在状态串行域内访问；Dispose 幂等 </summary>
    public sealed class InstanceLease<T> : IDisposable where T : class
    {
        private readonly IExecutionContext _context;
        private readonly InstancePool.PoolBucket _bucket;
        private readonly T _value;
        private readonly long _instanceId;
        private int _disposed;

        internal InstanceLease(IExecutionContext context, InstancePool.PoolBucket bucket, T value, long instanceId)
        {
            _context = context;
            _bucket = bucket;
            _value = value;
            _instanceId = instanceId;
        }

        /// <summary> 实例对象；在状态串行域外访问立即失败 </summary>
        public T Value
        {
            get
            {
                _context.AssertAccess();
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ResourceLoadException(_Invalidated());
                }
                return _value;
            }
        }

        /// <summary> 该租约是否仍然有效；不触碰引擎对象 </summary>
        public bool IsValid => Volatile.Read(ref _disposed) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _bucket.Owner.Return(_bucket, _value, _instanceId);
        }

        private LoadError _Invalidated() => new LoadError(
            DiagnosticCodes.InstanceUnsupported, LoadStage.ReturnInstance, CleanupStatus.Complete, null,
            new Dictionary<string, object> { { "pool", _bucket.Key.ToString() } });
    }
}
