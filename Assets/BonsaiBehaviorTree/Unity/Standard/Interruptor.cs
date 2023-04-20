namespace Bonsai.Standard
{
#if UNITY_EDITOR
    public partial class Interruptor
    {
        [UnityEngine.Tooltip("If true, then the interruptable node return success else failure.")]
        public bool returnSuccess = false;
    }
#endif  
}