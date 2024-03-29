﻿using System.Collections.Generic;
using System.Linq;
using Bonsai.Core;
using UnityEngine;

namespace Bonsai.Standard
{
    [BonsaiNode("Tasks/", "Interruptor")]
    public class Interruptor : Task
    {
        [Tooltip("If true, then the interruptable node return success else failure.")]
        public bool returnSuccess = false;

        public List<Interruptable> linkedInterruptables = new List<Interruptable>();

        public override NodeStatus Run()
        {
            for (int i = 0; i < linkedInterruptables.Count; ++i)
            {
                NodeStatus interruptionStatus = returnSuccess ? NodeStatus.Success : NodeStatus.Failure;
                linkedInterruptables[i].PerformInterruption(interruptionStatus);
            }

            return NodeStatus.Success;
        }

        public override void OnCopy()
        {
            // Only get the instance version of interruptables under the tree root.
            linkedInterruptables = linkedInterruptables
                .Where(i => i.PreOrderIndex != KInvalidOrder)
                .Select(i => BehaviourTree.GetInstanceVersion<Interruptable>(Tree, i))
                .ToList();
        }

        public override BehaviourNode[] GetReferencedNodes()
        {
            return linkedInterruptables.ToArray();
        }
    }
}