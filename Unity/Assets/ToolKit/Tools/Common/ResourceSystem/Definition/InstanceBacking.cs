/*
 * author       : Gordon
 * datetime     : 2026/6/27
 * description  : 实例型背书。背后是从 PooledInstanceSource 取出的实例 (当前为 GameObject)。
 *                释放即归还对象池; 不支持复制 (一个实例不能被两个所有者持有)。
 */

namespace ToolKit.Tools.Common
{
    internal sealed class InstanceBacking : IRefBacking
    {
        private readonly PooledInstanceSource _pool;
        private readonly string _address;
        private readonly object _instance;

        public InstanceBacking(PooledInstanceSource pool, string address, object instance)
        {
            _pool = pool;
            _address = address;
            _instance = instance;
        }

        public ERefKind Kind => ERefKind.Instance;
        public string Address => _address;

        public T Get<T>() where T : class
        {
            return (_instance != null && _pool.IsAlive(_instance)) ? _instance as T : null;
        }

        public bool IsAlive => _instance != null && _pool.IsAlive(_instance);

        public void Release() => _pool.Release(_address, _instance);

        // 实例不可被多个所有者独立持有, 需要新实例请重新 InstantiateRefAsync
        public IRefBacking AcquireClone() => null;
    }
}
