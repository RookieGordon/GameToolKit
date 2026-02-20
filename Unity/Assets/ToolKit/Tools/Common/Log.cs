using System.Diagnostics;

namespace ToolKit.Tools.Common
{
    public interface ILog
    {
        void Info(string message);
        void Info(string message, System.Object context);
        void Debug(string message);
        void Debug(string message, System.Object context);
        void Error(string message);
        void Error(string message, System.Object context);
        void Assert(bool condition, string message);
    }
    
    public class Log
    {
        private static ILog _log = null;
        
        public static void SetLog(ILog log)
        {
            _log = log;
        }

        public static void Info(string message)
        {
            _log?.Info(message);
        }
        
        public static void Info(string message, System.Object context)
        {
            _log?.Info(message, context);
        }
        
        [Conditional("DEBUG")]
        public static void Debug(string message)
        {
            _log?.Debug(message);
        }
        
        [Conditional("DEBUG")]
        public static void Debug(string message, System.Object context)
        {
            _log?.Debug(message, context);
        }
        
        public static void Error(string message)
        {
            _log?.Error(message);
        }
        
        public static void Error(string message, System.Object context)
        {
            _log?.Error(message, context);
        }
        
        [Conditional("DEBUG")]
        public static void Assert(bool condition, string message)
        {
            _log?.Assert(condition, message);
        }
    }
}