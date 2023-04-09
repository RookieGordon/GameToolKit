using UnityEngine;

namespace Bonsai.Utility
{
    public class Log
    {
        public static void LogWarning(string str)
        {
            Debug.LogWarning(str);
        }
        
        public static void LogInfo(string str)
        {
            Debug.Log(str);
        }
        
        public static void LogError(string str)
        {
            Debug.LogError(str);
        }
    }
}