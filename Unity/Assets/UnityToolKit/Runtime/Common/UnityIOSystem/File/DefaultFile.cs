using System.IO;

namespace UnityToolKit.Runtime.Common
{
    public class DefaultFile: IPlatformFile
    {
        public bool Exists(string filePath)
        {
            return File.Exists(filePath);
        }

        public byte[] ReadAllBytes(string filePath)
        {
            return File.ReadAllBytes(filePath);
        }

        public string ReadAllText(string filePath)
        {
            return File.ReadAllText(filePath);
        }

        public void WriteAllBytes(string filePath, byte[] bytes)
        {
            File.WriteAllBytes(filePath, bytes);
        }

        public void WriteAllText(string filePath, string contents)
        {
            File.WriteAllText(filePath, contents);
        }

        public void Delete(string filePath)
        {
            File.Delete(filePath);
        }

        public void Copy(string sourceFileName, string destFileName)
        {
            File.Copy(sourceFileName, destFileName);
        }
    }
}