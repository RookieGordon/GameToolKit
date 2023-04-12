using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class IdleProxy : TaskProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Idle);
        }
    }
}