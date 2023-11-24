using System.Collections.Generic;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks
{
    public abstract partial class ParentTask
    {
        [SerializeField] protected List<Task> children;
    }
}