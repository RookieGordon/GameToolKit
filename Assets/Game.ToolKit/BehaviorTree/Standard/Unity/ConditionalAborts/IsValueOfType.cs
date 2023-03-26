using UnityEngine;

namespace Bonsai.Standard
{
    public partial class IsValueOfType : ISerializationCallbackReceiver
    {
        [Tooltip("The key of the value to test its type.")]
        public string key;
        
        // Since Unity cannot serialize Type, we need to store the full name of the type.
        [SerializeField, HideInInspector] private string typename;
    }
}