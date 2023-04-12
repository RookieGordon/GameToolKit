using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class RepeaterProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Repeater);
        }
    }
}