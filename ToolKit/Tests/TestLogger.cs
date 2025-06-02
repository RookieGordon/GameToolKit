using ToolKit.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Test
{


    public class TestLogger : ILog
    {
        private readonly ITestOutputHelper _testOutputHelper;

        public TestLogger(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
        }

        public void Info(string message)
        {
            _testOutputHelper.WriteLine(message);
        }

        public void Info(string message, object context)
        {
            _testOutputHelper.WriteLine(message, context);
        }

        public void Debug(string message)
        {
            _testOutputHelper.WriteLine(message);
        }

        public void Debug(string message, object context)
        {
            _testOutputHelper.WriteLine(message, context);
        }

        public void Error(string message)
        {
            _testOutputHelper.WriteLine(message);
        }

        public void Error(string message, object context)
        {
            _testOutputHelper.WriteLine(message, context);
        }

        public void Assert(bool condition, string message)
        {

        }
    }
}