﻿using System.Text;
using Bonsai.Core;

namespace Bonsai.Standard
{
    /// <summary>
    /// Always returns running.
    /// </summary>
    [BonsaiNode("Tasks/", "Hourglass")]
    public class Idle : Core.Task
    {
        public override NodeStatus Run()
        {
            return NodeStatus.Running;
        }

        public override void Description(StringBuilder builder)
        {
            builder.Append("Run forever");
        }
    }
}