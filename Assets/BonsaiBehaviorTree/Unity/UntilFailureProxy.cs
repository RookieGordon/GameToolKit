using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class UntilFailureProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(UntilFailure);
        }
    }
}