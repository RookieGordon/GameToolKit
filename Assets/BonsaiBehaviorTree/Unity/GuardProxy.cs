using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class GuardProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Guard);
        }
    }
}