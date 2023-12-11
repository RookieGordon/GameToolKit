using Unity.Mathematics;

namespace BehaviorDesigner.Runtime
{
    [System.Serializable]
    public class SharedQuaternion : SharedVariable<quaternion>
    {
        public static implicit operator SharedQuaternion(quaternion value)
        {
            return new SharedQuaternion { mValue = value };
        }
    }
}