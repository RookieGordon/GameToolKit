/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 诊断接口与只读快照。最终失败只由操作负责人发布一次；快照不暴露可改变持有关系的条目对象，
 *                Row 只是不可变诊断记录 (只有 Key/State/HoldCount/WaitingCount，没有字节数与年龄)。
 */

using System;
using System.Collections.Generic;

namespace ToolKit.Tools.Common
{
    /// <summary> 诊断出口；实现抛出异常不得中断资源生命周期 </summary>
    public interface IResourceDiagnostics
    {
        /// <summary> 发布一次最终失败诊断 </summary>
        void Report(LoadError error);
    }

    /// <summary> 默认空实现：不记录任何内容 </summary>
    public sealed class NullResourceDiagnostics : IResourceDiagnostics
    {
        public static readonly NullResourceDiagnostics Instance = new NullResourceDiagnostics();

        public void Report(LoadError error)
        {
        }
    }

    /// <summary> 包装任意诊断实现：Report 永不抛出 </summary>
    public sealed class SafeResourceDiagnostics : IResourceDiagnostics
    {
        private readonly IResourceDiagnostics _inner;

        public SafeResourceDiagnostics(IResourceDiagnostics? inner)
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
    }

    /// <summary> 已登记产物的诊断状态；只由记录的真实状态投影，不单独存储 </summary>
    public enum ResourceState
    {
        /// <summary> 正在加载，新请求可一起等待这次结果。 </summary>
        Loading,

        /// <summary> 已被放弃，等待后端结束并收尾迟到产物；新请求只等它结束，不接收结果。 </summary>
        Draining,

        /// <summary> 资源可用且有人持有。 </summary>
        Ready,

        /// <summary> 没有引用，暂存于产物容器，可直接复用。 </summary>
        Idle,

        /// <summary> 正在释放底层资源。 </summary>
        Unloading,
    }

    /// <summary> 池桶状态 </summary>
    public enum PoolState
    {
        Opening,
        Open,
        Closed,
    }

    /// <summary> 管理器只读快照；不暴露可改变持有关系的对象 </summary>
    public sealed class ResourceSnapshot
    {
        public bool IsInitialized;
        public bool IsDisposed;
        public List<ResourceRow> ResourceRows = new List<ResourceRow>();
        public List<PoolRow> PoolRows = new List<PoolRow>();
        public List<DirtyResource> DirtyResources = new List<DirtyResource>();
    }

    public readonly struct ResourceRow
    {
        public readonly ResourceKey Key;
        public readonly ResourceState State;
        public readonly int HoldCount;
        public readonly int WaitingCount;

        public ResourceRow(ResourceKey key, ResourceState state, int holdCount, int waitingCount)
        {
            Key = key;
            State = state;
            HoldCount = holdCount;
            WaitingCount = waitingCount;
        }
    }

    public readonly struct PoolRow
    {
        public readonly PoolKey Key;
        public readonly PoolState State;
        public readonly int Active;
        public readonly int Idle;
        public readonly int InFlight;

        public PoolRow(PoolKey key, PoolState state, int active, int idle, int inFlight)
        {
            Key = key;
            State = state;
            Active = active;
            Idle = idle;
            InFlight = inFlight;
        }
    }
}
