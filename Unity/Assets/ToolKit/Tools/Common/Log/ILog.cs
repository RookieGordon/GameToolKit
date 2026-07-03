namespace ToolKit.Tools.Common
{
    public interface ILog
    {
        void Info(string message);
        void Info(string message, System.Object context);
        void Debug(string message);
        void Debug(string message, System.Object context);
        void Warn(string message);
        void Warn(string message, System.Object context);
        void Error(string message);
        void Error(string message, System.Object context);
        void Assert(bool condition, string message);
    }
}