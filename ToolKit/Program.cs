using ToolKit.Tools;

namespace ToolKit;

class Program
{
    static void Main(string[] args)
    {
        Log.SetLog(new ConsoleLogger());

        List<int> l1 = new List<int>()
        {
            1, 2, 3
        };
        List<int> l2 = l1;
        l1.Clear();
        Log.Debug($"{l1.Count}-->{l2.Count}");
    }
}