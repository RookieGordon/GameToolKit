using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class InterruptableProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Interruptable);
        }
    }
}