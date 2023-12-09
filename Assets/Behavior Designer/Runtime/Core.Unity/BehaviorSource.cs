using System;
using UnityEngine;

namespace BehaviorDesigner.Runtime
{
    [Serializable]
    public partial class BehaviorSource
    {
        [NonSerialized] private bool mHasSerialized;

        [SerializeField] private TaskSerializationData mTaskData;

        [SerializeField] private IBehavior mOwner;
    }
}