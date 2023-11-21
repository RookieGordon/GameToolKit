using UnityEngine;

namespace BehaviorDesigner.Runtime
{
    public partial class GenericVariable
    {
        [SerializeField]
        public string type = "SharedString";

        [SerializeField]
        public SharedVariable value;
    }
}
