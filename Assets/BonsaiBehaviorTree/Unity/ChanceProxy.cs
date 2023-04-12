using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class ChanceProxy : ConditionalTaskProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Chance);
        }
    }
}