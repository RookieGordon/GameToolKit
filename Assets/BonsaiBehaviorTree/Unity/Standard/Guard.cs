namespace Bonsai.Standard
{
#if UNITY_EDITOR
    public partial class Guard
    {
        [UnityEngine.Tooltip(@"If true, then the guard will stay running until the child can be used (active guard count < max active guards), else the guard will immediately return.")]
        public bool waitUntilChildAvailable = false;
        
        [UnityEngine.Tooltip("When the guard does not wait, should we return success of failure when skipping it?")]
        public bool returnSuccessOnSkip = false;
    }
#endif
}