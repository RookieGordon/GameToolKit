#if UNITY_PLATFORM
using GameObject = UnityEngine.GameObject;
#else
using GameObject = Entity;
#endif

namespace BehaviorDesigner.Runtime
{
    [System.Serializable]
    public class SharedGameObject : SharedVariable<GameObject>
    {
        public static implicit operator SharedGameObject(GameObject value)
        {
            return new SharedGameObject { mValue = value };
        }
    }
}