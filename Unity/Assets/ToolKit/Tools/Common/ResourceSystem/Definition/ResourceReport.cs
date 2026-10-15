/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 只读对外报告：诊断出口、资源与池快照、残留记录及一次卸载的结果。
 *                Row 只是不可变诊断记录 (Key/State/HoldCount/WaitingCount)，不暴露可改变持有关系的条目对象。
 *                残留与正常资源索引分离：不封锁 Key、不自动重试释放，也不由后续加载触发。
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

    /// <summary>
    /// 一次未确认结束的释放所留下的具体对象。RetainedObjects 只读，供排查使用；
    /// <b>不赋予释放权限、也不转移所有权</b>：责任仍由原产物记录或原池桶承担。
    /// 不得在不了解后续使用关系时再次销毁引擎资产。
    /// </summary>
    public sealed class DirtyResource
    {
        public ResourceKey Key { get; }
        public LoadError Error { get; }
        public IReadOnlyList<object> RetainedObjects { get; }

        public DirtyResource(ResourceKey key, LoadError error, IReadOnlyList<object> retainedObjects)
        {
            Key = key;
            Error = error ?? throw new ArgumentNullException(nameof(error));
            RetainedObjects = new List<object>(retainedObjects).AsReadOnly();
        }
    }

    /// <summary> 本次业务主动卸载的结果；跳过在用项，失败不阻止其他项继续释放。 </summary>
    public sealed class ResourceUnloadResult
    {
        public int ReleasedCount { get; }
        public int SkippedInUseCount { get; }
        public IReadOnlyList<DirtyResource> Failures { get; }

        public ResourceUnloadResult(int releasedCount, int skippedInUseCount, IReadOnlyList<DirtyResource> failures)
        {
            ReleasedCount = releasedCount;
            SkippedInUseCount = skippedInUseCount;
            Failures = new List<DirtyResource>(failures).AsReadOnly();
        }
    }
}
