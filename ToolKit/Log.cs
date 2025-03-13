using ToolKit.Tools;

namespace ToolKit;

public class Log
{
    public static ILog _Log;
    
    public static void SetLog(ILog log)
    {
        _Log = log;
    }
    
    public static void Info(string message)
    {
        _Log?.Info(message);
    }
    
    public static void Info(string message, object context)
    {
        _Log?.Info(message, context);
    }
    
    public static void Debug(string message)
    {
        _Log?.Debug(message);
    }

    public static void Debug(string message, object context)
    {
        _Log?.Debug(message, context);
    }
    
    public static void Error(string message)
    {
        _Log?.Error(message);
    }
    
    public static void Error(string message, object context)
    {
        _Log?.Error(message, context);
    }
    
    public static void Assert(bool condition, string message)
    {
        _Log?.Assert(condition, message);
    }
}