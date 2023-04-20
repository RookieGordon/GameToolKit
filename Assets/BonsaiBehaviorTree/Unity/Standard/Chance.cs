namespace Bonsai.Standard
{
#if UNITY_EDITOR
    public partial class Chance
    {
        [UnityEngine.Tooltip("The probability that the condition succeeds.")] [UnityEngine.Range(0f, 1f)]
        public float chance = 0.5f;
    }
#endif
}