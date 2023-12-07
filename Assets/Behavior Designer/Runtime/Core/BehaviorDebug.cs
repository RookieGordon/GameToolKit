namespace BehaviorDesigner.Runtime
{
    public partial class BehaviorDebug
    {
        public static void Log(string message)
        {
            UnityEngine.Debug.Log((object)message);
        }

        public static void Log(object message)
        {
            UnityEngine.Debug.Log(message);
        }

        public static void LogWarning(string message)
        {
            UnityEngine.Debug.LogWarning((object)message);
        }

        public static void LogError(string message)
        {
            UnityEngine.Debug.LogError((object)message);
        }

        public static void LogError(object message)
        {
            UnityEngine.Debug.LogError(message);
        }
    }
}