using UnityEngine;

namespace Bonsai.Core
{
    public abstract partial class Composite
    {
        [SerializeField, HideInInspector] private BehaviourNode[] _children;
    }
}