namespace Bonsai.Standard
{
#if UNITY_EDITOR
    public partial class CompareEntries
    {
        [UnityEngine.Tooltip("If the comparison should test for inequality")]
        public bool compareInequality = false;
    }
#endif
}