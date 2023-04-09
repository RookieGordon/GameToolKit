using Bonsai.Core;
using System;
using UnityEngine;

namespace Bonsai.Standard
{
    public class ParallelProxy : ParallelCompositeProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Parallel);
        }
        
        // public override void ConnectToProxy(BehaviourNode node)
        // {
        //     Node = node as Parallel;
        //     Node.Proxy = this;
        // }
    }

    public class ParallelSelectorProxy : ParallelProxy
    {
        public override Type GetNodeType()
        {
            return typeof(ParallelSelector);
        }
        
        // public override void ConnectToProxy(BehaviourNode node)
        // {
        //     Node = node as ParallelSelector;
        //     Node.Proxy = this;
        // }
    }

    public class RandomSelectorProxy : SelectorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(RandomSelector);
        }
        
        // public override void ConnectToProxy(BehaviourNode node)
        // {
        //     Node = node as RandomSelector;
        //     Node.Proxy = this;
        // }
    }

    public class RandomSequenceProxy : SequenceProxy
    {
        public override Type GetNodeType()
        {
            return typeof(RandomSequence);
        }
        
        // public override void ConnectToProxy(BehaviourNode node)
        // {
        //     Node = node as RandomSequence;
        //     Node.Proxy = this;
        // }
    }

    public class SelectorProxy : CompositeProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Selector);
        }
        
        // public override void ConnectToProxy(BehaviourNode node)
        // {
        //     Node = node as Selector;
        //     Node.Proxy = this;
        // }
    }

    public class SequenceProxy : CompositeProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Sequence);
        }
        
        // public override void ConnectToProxy(BehaviourNode node)
        // {
        //     Node = node as Sequence;
        //     Node.Proxy = this;
        // }
    }

    public class UtilitySelectorProxy : SelectorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(UtilitySelector);
        }
        
        // public override void ConnectToProxy(BehaviourNode node)
        // {
        //     Node = node as UtilitySelector;
        //     Node.Proxy = this;
        // }
    }

    public class CompareEntriesProxy : ConditionalAbortProxy
    {
        public override Type GetNodeType()
        {
            return typeof(CompareEntries);
        }
        
        // public override void ConnectToProxy(BehaviourNode node)
        // {
        //     Node = node as CompareEntries;
        //     Node.Proxy = this;
        // }
    }

    public class CooldownProxy : ConditionalAbortProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Cooldown);
        }
    }

    public class IsValueOfTypeProxy : ConditionalAbortProxy
    {
        public override Type GetNodeType()
        {
            return typeof(IsValueOfType);
        }
    }

    public class IsValueSetProxy : ConditionalAbortProxy
    {
        public override Type GetNodeType()
        {
            return typeof(IsValueSet);
        }
    }

    public class TimeLimitProxy : ConditionalAbortProxy
    {
        public override Type GetNodeType()
        {
            return typeof(TimeLimit);
        }
    }

    public class ChanceProxy : ConditionalTaskProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Chance);
        }
    }

    public class FailureProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Failure);
        }
    }
    
    public class InterruptableProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Interruptable);
        }
    }
    
    public class GuardProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Guard);
        }
    }
    
    public class InterruptorProxy : TaskProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Interruptor);
        }
    }
    
    public class InverterProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Inverter);
        }
    }
    
    public class RepeaterProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Repeater);
        }
    }
    
    public class SuccessProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Success);
        }
        
        // public override void ConnectToProxy(BehaviourNode node)
        // {
        //     Node = node as Success;
        //     Node.Proxy = this;
        // }
    }
    
    public class UntilFailureProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(UntilFailure);
        }
    }
    
    public class UntilSuccessProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(UntilSuccess);
        }
    }
    
    public class IdleProxy : TaskProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Idle);
        }
    }
    
    public class PrintProxy : TaskProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Print);
        }
        
        // public override void ConnectToProxy(BehaviourNode node)
        // {
        //     Node = node as Print;
        //     Node.Proxy = this;
        // }
    }
    
    public class IncludeProxy : TaskProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Include);
        }
    }
    
    public class WaitProxy : TaskProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Wait);
        }
    }
}