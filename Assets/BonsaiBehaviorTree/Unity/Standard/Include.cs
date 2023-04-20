using Bonsai.Core;

namespace Bonsai.Standard
{
#if UNITY_EDITOR
    public partial class Include
    {
        [UnityEngine.Tooltip("The sub-tree to run when this task executes.")]
        public BehaviourTree subtreeAsset;
    }
#endif
}