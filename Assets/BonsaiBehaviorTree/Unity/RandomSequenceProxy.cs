using System;

namespace Bonsai.Standard
{
    public class RandomSequenceProxy : SequenceProxy
    {
        public override Type GetNodeType()
        {
            return typeof(RandomSequence);
        }
    }
}