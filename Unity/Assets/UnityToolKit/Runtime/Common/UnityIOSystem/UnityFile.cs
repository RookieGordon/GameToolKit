using System;
using System.IO;

namespace UnityToolKit.Runtime.Common
{
    public class UnityFile
    {
        private static IPlatformFile _platformFile => new DefaultFile();

        public static bool Exists(string filePath)
        {
            return _platformFile.Exists(filePath);
        }

        public static byte[] ReadAllBytes(string filePath)
        {
            return _platformFile.ReadAllBytes(filePath);
        }

        public static string ReadAllText(string filePath)
        {
            return _platformFile.ReadAllText(filePath);
        }

        public static void WriteAllBytes(string filePath, byte[] bytes)
        {
            _platformFile.WriteAllBytes(filePath, bytes);
        }

        public static void WriteAllText(string filePath, string contents)
        {
            _platformFile.WriteAllText(filePath, contents);
        }

        public static void Delete(string filePath)
        {
            _platformFile.Delete(filePath);
        }

        public static void Copy(string sourceFileName, string destFileName)
        {
            _platformFile.Copy(sourceFileName, destFileName);
        }
    }
}