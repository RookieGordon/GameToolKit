using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class SequenceProxy : CompositeProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Sequence);
        }
    }
}