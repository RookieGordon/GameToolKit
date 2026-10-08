/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 诊断接口与只读快照 (P0)。最终失败只由操作负责人发布一次 (按 DiagnosticId 去重)；
 *                快照不暴露可改变持有关系的条目对象。Row 仅为不可变诊断记录。
 */

using System;
using System.Collections.Generic;

namespace ToolKit.Tools.Common.Resource
{
    public interface IResourceDiagnostics
    {
        /// <summary> 发布一次最终失败诊断 (同一 DiagnosticId 只发布一次) </summary>
        void Report(LoadError error);

        /// <summary> 记录过程事件 (尝试、回收、状态迁移等；不用于故障判定) </summary>
        void Trace(ResourceEvent traceEvent);
    }

    /// <summary> 默认空实现：不记录任何内容 </summary>
    public sealed class NullResourceDiagnostics : IResourceDiagnostics
    {
        public static readonly NullResourceDiagnostics Instance = new NullResourceDiagnostics();

        public void Report(LoadError error)
        {
        }

        public void Trace(ResourceEvent traceEvent)
        {
        }
    }

    public readonly struct ResourceEvent
    {
        public readonly string Name;
        public readonly string OperationId;
        public readonly IReadOnlyDictionary<string, object> Data;

        public ResourceEvent(string name, string operationId, IReadOnlyDictionary<string, object>? data = null)
        {
            Name = name;
            OperationId = operationId;
            Data = data ?? EmptyData;
        }

        private static readonly IReadOnlyDictionary<string, object> EmptyData =
            new Dictionary<string, object>();
    }

    /// <summary> 管理器只读快照 </summary>
    public sealed class ResourceSnapshot
    {
        public ManagerState State;
        public int ActiveRequests;
        public List<ResourceRow> ResourceRows = new List<ResourceRow>();
        public List<PoolRow> PoolRows = new List<PoolRow>();
        public List<LoadError> CleanupErrors = new List<LoadError>();
    }

    public readonly struct ResourceRow
    {
        public readonly ResourceKey Key;
        public readonly ResourceState State;
        public readonly int HoldCount;
        public readonly int WaitingCount;
        public readonly long EstimatedBytes;

        public ResourceRow(ResourceKey key, ResourceState state, int holdCount, int waitingCount, long estimatedBytes)
        {
            Key = key;
            State = state;
            HoldCount = holdCount;
            WaitingCount = waitingCount;
            EstimatedBytes = estimatedBytes;
        }
    }

    public readonly struct PoolRow
    {
        public readonly PoolKey Key;
        public readonly long Generation;
        public readonly PoolState State;
        public readonly int Active;
        public readonly int Idle;
        public readonly int InFlight;

        public PoolRow(PoolKey key, long generation, PoolState state, int active, int idle, int inFlight)
        {
            Key = key;
            Generation = generation;
            State = state;
            Active = active;
            Idle = idle;
            InFlight = inFlight;
        }
    }
}
