using Unity.Mathematics;

namespace BehaviorDesigner.Runtime
{
    [System.Serializable]
    public class SharedVector4 : SharedVariable<float4>
    {
        public static implicit operator SharedVector4(float4 value)
        {
            return new SharedVector4 { mValue = value };
        }
    }
}