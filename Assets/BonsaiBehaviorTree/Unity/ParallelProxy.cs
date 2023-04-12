using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class ParallelProxy : ParallelCompositeProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Parallel);
        }
    }
}