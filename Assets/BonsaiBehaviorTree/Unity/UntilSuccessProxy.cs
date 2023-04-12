using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class UntilSuccessProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(UntilSuccess);
        }
    }
}