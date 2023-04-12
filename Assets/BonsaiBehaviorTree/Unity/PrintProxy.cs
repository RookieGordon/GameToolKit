using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class PrintProxy : TaskProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Print);
        }
    }
}