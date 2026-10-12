/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 资源身份键 (P0, §3.3)。ResourceKey = Source + 加载器返回的 LocalKey，
 *                比较规则 Ordinal，不统一改小写或删查询参数；加载器必须保证相同键得到
 *                兼容的资源表示、版本和底层释放规则。PoolKey 在原型键之上叠加工厂与实例表示标识。
 */

using System;

namespace ToolKit.Tools.Common
{
    /// <summary> 资源身份：资源来源 (Source) + 加载器解析身份 (LocalKey) </summary>
    public readonly struct ResourceKey : IEquatable<ResourceKey>
    {
        public readonly ResourceSource Source;
        public readonly string LocalKey;

        public ResourceKey(ResourceSource source, string localKey)
        {
            Source = source;
            LocalKey = localKey ?? "";
        }

        public bool Equals(ResourceKey other)
        {
            return Source == other.Source
                   && string.Equals(LocalKey, other.LocalKey, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is ResourceKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Source * 397) ^ LocalKey.GetHashCode();
            }
        }

        public override string ToString()
        {
            return Source + ":" + LocalKey;
        }
    }

    /// <summary> 池身份：原型 ResourceKey + FactoryId + 实例表示标识 (工厂 GetInstanceKey 提供) </summary>
    public readonly struct PoolKey : IEquatable<PoolKey>
    {
        public readonly ResourceKey Prototype;
        public readonly string FactoryId;
        public readonly string InstanceKey;

        public PoolKey(ResourceKey prototype, string factoryId, string instanceKey)
        {
            Prototype = prototype;
            FactoryId = factoryId ?? "";
            InstanceKey = instanceKey ?? "";
        }

        public bool Equals(PoolKey other)
        {
            return Prototype.Equals(other.Prototype)
                   && string.Equals(FactoryId, other.FactoryId, StringComparison.Ordinal)
                   && string.Equals(InstanceKey, other.InstanceKey, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is PoolKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Prototype.GetHashCode();
                hash = (hash * 397) ^ FactoryId.GetHashCode();
                hash = (hash * 397) ^ InstanceKey.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
        {
            return Prototype + "|" + FactoryId + "|" + InstanceKey;
        }
    }
}
