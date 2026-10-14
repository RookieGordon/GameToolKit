/*
 * author       : Gordon
 * datetime     : 2026-10-14
 * description  : 一次释放失败留下的具体对象与本次业务卸载的结果。
 *                脏记录与正常资源索引分离：不封锁 Key、不自动重试释放，也不由后续加载触发。
 */

using System;
using System.Collections.Generic;

namespace ToolKit.Tools.Common
{
    /// <summary>
    /// 一次释放失败留下的具体对象。RetainedObjects 仅供排查；
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
