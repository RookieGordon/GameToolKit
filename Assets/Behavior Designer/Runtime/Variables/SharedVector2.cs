using Unity.Mathematics;

namespace BehaviorDesigner.Runtime
{
    [System.Serializable]
    public class SharedVector2 : SharedVariable<float2>
    {
        public static implicit operator SharedVector2(float2 value)
        {
            return new SharedVector2 { mValue = value };
        }
    }
}