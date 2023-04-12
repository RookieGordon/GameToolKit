using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class IsValueOfTypeProxy : ConditionalAbortProxy
    {
        public override Type GetNodeType()
        {
            return typeof(IsValueOfType);
        }
    }
}