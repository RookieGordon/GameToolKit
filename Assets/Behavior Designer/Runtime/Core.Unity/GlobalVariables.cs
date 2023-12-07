using System.Collections.Generic;
using UnityEngine;

namespace BehaviorDesigner.Runtime
{
    public partial class GlobalVariables : ScriptableObject
    {
        [SerializeField] private List<SharedVariable> mVariables;

        [SerializeField] private VariableSerializationData mVariableData;

        [SerializeField] private string mVersion;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            GlobalVariables.instance = (GlobalVariables)null;
        }
    }
}