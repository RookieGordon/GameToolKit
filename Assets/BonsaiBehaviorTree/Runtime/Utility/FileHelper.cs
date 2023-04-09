using System.IO;

namespace Bonsai.Utility
{
    public class FileHelper
    {
        public static void WriteFile(string filePath, string content)
        {
            File.WriteAllText(filePath, content);
        }
        
        public static string ReadFile(string filePath)
        {
            return File.ReadAllText(filePath);
        }
    }
}