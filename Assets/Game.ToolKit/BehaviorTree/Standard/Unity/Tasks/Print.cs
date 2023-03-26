using UnityEngine;

namespace Bonsai.Standard
{
    public partial class Print
    {
        [Multiline] public string message = "Print Node";

        [Tooltip("The type of message to display.")]
        public LogType logType = LogType.Normal;
    }
}