using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class InterruptorProxy : TaskProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Interruptor);
        }
    }
}