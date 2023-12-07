using System.Collections.Generic;
using UnityEngine;

namespace BehaviorDesigner.Runtime
{
    [AddComponentMenu("Behavior Designer/Variable Synchronizer")]
    public partial class VariableSynchronizer : MonoBehaviour
    {
        [SerializeField] private UpdateIntervalType updateInterval;

        [SerializeField] private float updateIntervalSeconds;

        [SerializeField] private List<VariableSynchronizer.SynchronizedVariable> synchronizedVariables =
            new List<VariableSynchronizer.SynchronizedVariable>();
    }
}