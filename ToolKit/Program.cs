using ToolKit.Tools;

namespace ToolKit;

class Program
{
    static void Main(string[] args)
    {
        Log.SetLog(new ConsoleLogger());
    }
}