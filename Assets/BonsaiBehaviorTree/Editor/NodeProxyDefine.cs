using System;
using System.Collections.Generic;
using Bonsai.Core;
using Bonsai.Standard;

namespace Bonsai.Designer
{
    public class NodeProxyDefine
    {
        public static Dictionary<Type, Type> ProxyDic = new Dictionary<Type, Type>()
        {
            { typeof(BehaviourNode), typeof(BehaviourNodeProxy) },
            { typeof(Composite), typeof(CompositeProxy) },
            { typeof(ConditionalAbort), typeof(ConditionalAbortProxy) },
            { typeof(ConditionalTask), typeof(ConditionalTaskProxy) },
            { typeof(Decorator), typeof(DecoratorProxy) },
            { typeof(Task), typeof(TaskProxy) },
            { typeof(ParallelComposite), typeof(ParallelCompositeProxy) },
            { typeof(Service), typeof(ServiceProxy) },
            { typeof(Parallel), typeof(ParallelProxy) },
            { typeof(ParallelSelector), typeof(ParallelSelectorProxy) },
            { typeof(RandomSelector), typeof(RandomSelectorProxy) },
            { typeof(RandomSequence), typeof(RandomSequenceProxy) },
            { typeof(Selector), typeof(SelectorProxy) },
            { typeof(Sequence), typeof(SequenceProxy) },
            { typeof(UtilitySelector), typeof(UtilitySelectorProxy) },
            { typeof(CompareEntries), typeof(CompareEntriesProxy) },
            { typeof(Cooldown), typeof(CooldownProxy) },
            { typeof(IsValueOfType), typeof(IsValueOfTypeProxy) },
            { typeof(IsValueSet), typeof(IsValueSetProxy) },
            { typeof(TimeLimit), typeof(TimeLimitProxy) },
            { typeof(Chance), typeof(ChanceProxy) },
            { typeof(Failure), typeof(FailureProxy) },
            { typeof(Interruptable), typeof(Interruptable) },
            { typeof(Guard), typeof(GuardProxy) },
            { typeof(Interruptor), typeof(InterruptorProxy) },
            { typeof(Inverter), typeof(InverterProxy) },
            { typeof(Repeater), typeof(RepeaterProxy) },
            { typeof(Success), typeof(SuccessProxy) },
            { typeof(UntilFailure), typeof(UntilFailureProxy) },
            { typeof(UntilSuccess), typeof(UntilSuccessProxy) },
            { typeof(Idle), typeof(IdleProxy) },
            { typeof(Print), typeof(PrintProxy) },
            { typeof(Include), typeof(IncludeProxy) },
            { typeof(Wait), typeof(WaitProxy) },
        };
    }
}