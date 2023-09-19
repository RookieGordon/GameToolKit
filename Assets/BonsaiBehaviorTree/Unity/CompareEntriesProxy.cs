using System;
using Bonsai.Core;
using UnityEngine;

namespace Bonsai.Standard
{
    public class CompareEntriesProxy : ConditionalAbortProxy
    {
        [HideInInspector, Multiline]
        public readonly string functionalDesc = "条件终止类型节点 \n" +
                                                "执行到该节点时，会比较a和b两个key的值是否相等（或不相等），比较通过则会在下一帧立刻执行其子节点。如果有设置中断类型，则在执行该节点后，如果key值发生改变，会再次进行比较，根据结果和中断类型，中断对应分支的执行。\n" +
                                                "中断类型：" +
                                                "1、Self或Both中断类型：节点执行中，条件检查不通过，中断以父节点为根的子树，并且会在下一帧重新执行当前节点所在的分支；\n" +
                                                "2、LowerPriority或者Both中断类型：节点退出后，条件检查通过并且，向上找到第一个Composite节点，记当前分支序号为a, 如果以该Composite节点为根的树正在执行的分支b大于a，那么就中断树的执行，并且下一帧重新从a分支执行。";

        public override Type GetNodeType()
        {
            return typeof(CompareEntries);
        }
    }
}