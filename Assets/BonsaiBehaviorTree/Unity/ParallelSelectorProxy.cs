using System;

namespace Bonsai.Standard
{
    public class ParallelSelectorProxy : ParallelProxy
    {
        public override Type GetNodeType()
        {
            return typeof(ParallelSelector);
        }
    }
}