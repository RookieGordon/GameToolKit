using UnityEngine;

namespace Bonsai.Core
{
    public abstract partial class Decorator
    {
        [SerializeField, HideInInspector] protected BehaviourNode _child;
    }
}