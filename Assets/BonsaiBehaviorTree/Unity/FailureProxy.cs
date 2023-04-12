using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class FailureProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Failure);
        }
    }
}