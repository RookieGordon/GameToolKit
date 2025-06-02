using ToolKit.Tools;
using System;

namespace ToolKit
{
    public class ConsoleLogger : ILog
    {
        public void Info(string message)
        {
            Console.WriteLine(message);
        }

        public void Info(string message, object context)
        {
            Console.WriteLine(message, context);
        }

        public void Debug(string message)
        {
            Console.WriteLine(message);
        }

        public void Debug(string message, object context)
        {
            Console.WriteLine(message, context);
        }

        public void Error(string message)
        {
            Console.WriteLine(message);
        }

        public void Error(string message, object context)
        {
            Console.WriteLine(message, context);
        }

        public void Assert(bool condition, string message)
        {
            System.Diagnostics.Debug.Assert(condition, message);
        }
    }
}