using System;

namespace Bonsai.Standard
{
    public class UtilitySelectorProxy : SelectorProxy
    {
        public override Type GetNodeType()
        {
            return typeof(UtilitySelector);
        }
    }
}