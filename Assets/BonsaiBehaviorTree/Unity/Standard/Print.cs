namespace Bonsai.Standard
{
#if UNITY_EDITOR
    public partial class Print
    {
        [UnityEngine.Multiline] public string message = "Print Node";

        [UnityEngine.Tooltip("The type of message to display.")]
        public LogType logType = LogType.Normal;
    }
#endif
}