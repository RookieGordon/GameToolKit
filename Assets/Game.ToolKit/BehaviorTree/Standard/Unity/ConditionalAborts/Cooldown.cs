namespace Bonsai.Standard
{
    public partial class Cooldown
    {
        [ShowAtRuntime] [UnityEngine.SerializeField]
        public Utility.Timer timer = new Utility.Timer();
    }
}