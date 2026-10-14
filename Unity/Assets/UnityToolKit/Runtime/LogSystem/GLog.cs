using ToolKit.Tools.Common;

namespace UnityToolKit.Runtime.LogSystem
{
    public class GLog
    {
        private static Logger _logger;
        
        public static void Info(string message)
        {
            _logger.Info(message);
        }

        public static void Info(string message, object context)
        {
            _logger.Info(message, context);
        }

        public static void Debug(string message)
        {
            _logger.Debug(message);
        }

        public static void Debug(string message, object context)
        {
            _logger.Debug(message, context);
        }

        public static void Warn(string message)
        {
            _logger.Warn(message);
        }

        public static void Warn(string message, object context)
        {
            _logger.Warn(message, context);
        }

        public static void Error(string message)
        {
            _logger.Error(message);
        }

        public static void Error(string message, object context)
        {
            _logger.Error(message, context);
        }

        public static void Assert(bool condition, string message)
        {
            _logger.Assert(condition, message);
        }
    }
}