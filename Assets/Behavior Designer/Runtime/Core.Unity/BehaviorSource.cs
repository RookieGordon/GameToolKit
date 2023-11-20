using System;
using UnityEngine;

namespace BehaviorDesigner.Runtime
{
    public partial class BehaviorSource
    {
        [NonSerialized]
        private bool mHasSerialized;

        [SerializeField]
        private TaskSerializationData mTaskData;

        [SerializeField]
        private IBehavior mOwner;
    }
}
