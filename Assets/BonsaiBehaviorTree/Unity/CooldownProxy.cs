using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class CooldownProxy : ConditionalAbortProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Cooldown);
        }
    }
}