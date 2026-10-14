using System.IO;

namespace UnityToolKit.Runtime.Common
{
    public class DefaultDirectory: IPlatformDirectory
    {
        public bool Exists(string path)
        {
            return Directory.Exists(path);
        }

        public void CreateDirectory(string path)
        {
            Directory.CreateDirectory(path);
        }

        public void Delete(string path)
        {
            Directory.Delete(path);
        }

        public string[] GetFiles(string path)
        {
            return Directory.GetFiles(path);
        }
        
        public string[] GetFiles(string path, string searchPattern)
        {
            return Directory.GetFiles(path, searchPattern);
        }
        
        public string[] GetFiles(string path, string searchPattern, System.IO.SearchOption searchOption)
        {
            return Directory.GetFiles(path, searchPattern, searchOption);
        }

        public string[] GetDirectories(string path)
        {
            return Directory.GetDirectories(path);
        }

        public string[] GetDirectories(string path, string searchPattern)
        {
            return Directory.GetDirectories(path, searchPattern);
        }

        public string[] GetDirectories(string path, string searchPattern, System.IO.SearchOption searchOption)
        {
            return Directory.GetDirectories(path, searchPattern, searchOption);
        }
    }
}