using UnityEngine;
using Bonsai.Core;

namespace Bonsai.Standard
{
    public partial class Include
    {
        [Tooltip("The sub-tree to run when this task executes.")]
        public BehaviourTree subtreeAsset;
    }
}