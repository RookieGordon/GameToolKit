using System;

namespace Bonsai.Standard
{
    public class RandomSelectorProxy : SelectorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(RandomSelector);
        }
    }
}