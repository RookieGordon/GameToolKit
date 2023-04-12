using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class TimeLimitProxy : ConditionalAbortProxy
    {
        public override Type GetNodeType()
        {
            return typeof(TimeLimit);
        }
    }
}