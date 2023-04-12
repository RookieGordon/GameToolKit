using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class IsValueSetProxy : ConditionalAbortProxy
    {
        public override Type GetNodeType()
        {
            return typeof(IsValueSet);
        }
    }
}