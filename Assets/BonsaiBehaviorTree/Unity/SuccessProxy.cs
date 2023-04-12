using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class SuccessProxy : DecoratorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Success);
        }
    }
}