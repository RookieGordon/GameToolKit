using Unity.Mathematics;

namespace BehaviorDesigner.Runtime
{
    [System.Serializable]
    public class SharedVector3 : SharedVariable<float3>
    {
        public static implicit operator SharedVector3(float3 value)
        {
            return new SharedVector3 { mValue = value };
        }
    }
}