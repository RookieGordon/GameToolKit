using Unity.Mathematics;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks.Unity.UnityCircleCollider2D
{
    [TaskCategory("Unity/CircleCollider2D")]
    [TaskDescription("Stores the offset of the CircleCollider2D. Returns Success.")]
    public class GetOffset : Action
    {
        [Tooltip("The GameObject that the task operates on. If null the task GameObject is used.")]
        public SharedGameObject targetGameObject;

        [Tooltip("The offset of the CircleCollider2D")] [RequiredField]
        public SharedVector3 storeValue;

        private CircleCollider2D circleCollider2D;
        private GameObject prevGameObject;

        public override void OnStart()
        {
            var currentGameObject = GetDefaultGameObject(targetGameObject.Value);
            if (currentGameObject != prevGameObject)
            {
                circleCollider2D = currentGameObject.GetComponent<CircleCollider2D>();
                prevGameObject = currentGameObject;
            }
        }

        public override TaskStatus OnUpdate()
        {
            if (circleCollider2D == null)
            {
                Debug.LogWarning("CircleCollider2D is null");
                return TaskStatus.Failure;
            }
            
            storeValue.Value = new float3(circleCollider2D.offset.x, circleCollider2D.offset.y, 0);

            return TaskStatus.Success;
        }

        public override void OnReset()
        {
            targetGameObject = null;
            storeValue = float3.zero;
        }
    }
}