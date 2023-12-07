using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks
{
    public abstract partial class Composite
    {
        [Tooltip("Specifies the type of conditional abort. More information is located at https://www.opsive.com/support/documentation/behavior-designer/conditional-aborts/.")] [SerializeField]
        protected AbortType abortType;
    }
}