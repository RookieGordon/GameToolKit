using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class InverterProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Inverter);
        }
    }
}