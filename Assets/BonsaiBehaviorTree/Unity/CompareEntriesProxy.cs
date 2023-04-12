using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public class CompareEntriesProxy : ConditionalAbortProxy
    {
        public override Type GetNodeType()
        {
            return typeof(CompareEntries);
        }
    }
}