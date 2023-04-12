using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class WaitProxy : TaskProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Wait);
        }
    }
}