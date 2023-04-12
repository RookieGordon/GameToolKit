using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class IncludeProxy : TaskProxy
    {
        public override Type GetNodeType()
        {
            return typeof(Include);
        }
    }
}