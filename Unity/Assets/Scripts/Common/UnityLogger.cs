using ToolKit.Tools;

public class UnityLogger: ILog
{
    public void Info(string message)
    {
        UnityEngine.Debug.Log(message);
    }

    public void Info(string message, System.Object context)
    {
        UnityEngine.Debug.Log(message, context as UnityEngine.Object);
    }

    public void Debug(string message)
    {
        UnityEngine.Debug.Log(message);
    }

    public void Debug(string message, System.Object context)
    {
        UnityEngine.Debug.Log(message, context as UnityEngine.Object);
    }

    public void Error(string message)
    {
        UnityEngine.Debug.LogError(message);
    }

    public void Error(string message, System.Object context)
    {
        UnityEngine.Debug.LogError(message, context as UnityEngine.Object);
    }

    public void Assert(bool condition, string message)
    {
        UnityEngine.Debug.Assert(condition, message);
    }
}