using System.Text;
using Bonsai.Core;
using Bonsai.Utility;

namespace Bonsai.Standard
{
    /// <summary>
    /// Displays a message.
    /// </summary>
    [BonsaiNode("Tasks/", "Log")]
    public  class Print : Task
    {
        public enum LogType
        {
            Normal,
            Warning,
            Error
        };

#if UNITY_EDITOR
        [UnityEngine.Multiline] 
#endif
        public string message = "Print Node";
        
#if UNITY_EDITOR
        [UnityEngine.Tooltip("The type of message to display.")]
#endif
        public LogType logType = LogType.Normal;

        public override NodeStatus Run()
        {
            switch (logType)
            {
                case LogType.Normal:
                    Log.LogInfo(message);
                    break;

                case LogType.Warning:
                    Log.LogWarning(message);
                    break;

                case LogType.Error:
                    Log.LogError(message);
                    break;
            }

            return NodeStatus.Success;
        }

        public override void Description(StringBuilder builder)
        {
            // Nothing to display.
            if (message.Length == 0)
            {
                return;
            }

            string displayed = message;

            // Only consider display the message up to the newline.
            int newLineIndex = message.IndexOf('\n');
            if (newLineIndex >= 0)
            {
                displayed = message.Substring(0, newLineIndex);
            }

            // Nothing to display.
            if (displayed.Length == 0)
            {
                return;
            }

            if (logType != LogType.Normal)
            {
                builder.AppendLine(logType.ToString());
            }

            // Cap the message length to display to keep things compact.
            int maxCharacters = 20;
            if (displayed.Length > maxCharacters)
            {
                builder.Append(displayed.Substring(0, maxCharacters));
                builder.Append("...");
            }
            else
            {
                builder.Append(displayed);
            }
        }
    }
}