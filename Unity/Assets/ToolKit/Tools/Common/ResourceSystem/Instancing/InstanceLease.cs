/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 实例租用凭证 (P3, §7)。非池化 sealed class，记录实例及其所属的具体池桶；
 *                Dispose 幂等，归还原桶 (绑定创建时的代际，不按地址重找)；不提供 Retain。
 *                外部销毁的实例仍需租约归还才能删除 Active 记录。
 */

using System;
using System.Threading;

namespace ToolKit.Tools.Common
{
    public sealed class InstanceLease<T> : IDisposable where T : class
    {
        private readonly InstancePool _pool;
        private readonly PoolBucket _bucket;
        private readonly InstanceRecord _record;
        private int _disposed;

        internal InstanceLease(InstancePool pool, PoolBucket bucket, InstanceRecord record)
        {
            _pool = pool;
            _bucket = bucket;
            _record = record;
        }

        /// <summary> 租用的实例。已归还/销毁后访问抛 ObjectDisposedException </summary>
        public T Value
        {
            get
            {
                _pool.Context.AssertAccess();
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(InstanceLease<T>),
                        $"实例租约已归还，不能再访问 Value (record={_record.LeaseId}, pool={_bucket.Key})");
                }
                return (T)_record.Value;
            }
        }

        /// <summary> 租约是否仍持有该实例 (未被归还) </summary>
        public bool IsValid
        {
            get
            {
                _pool.Context.AssertAccess();
                return Volatile.Read(ref _disposed) == 0 && _record.State == InstanceState.Active;
            }
        }

        /// <summary> 幂等；归还原池；一次性门闩保证只归还一次 </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            var bucket = _bucket;
            var record = _record;
            _pool.Context.Post(() => _pool.Return(bucket, record));
        }

        public override string ToString()
        {
            return $"InstanceLease<{typeof(T).Name}>(record={_record.LeaseId}, pool={_bucket.Key}, disposed={Volatile.Read(ref _disposed) != 0})";
        }
    }
}
