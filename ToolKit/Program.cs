namespace ToolKit;

class Program
{

    class MyClass
    {
        public string Name { get; set; }
    }
    static void Main(string[] args)
    {
        var arr = new MyClass[5];
        var t = arr[^1];
        if (t == null)
        {
            arr[^1] = new MyClass();
        }
        Console.WriteLine($"{arr[^1] == null}, {t == null}");
    }
}
