using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class SelectorProxy : CompositeProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Selector);
        }
    }
}