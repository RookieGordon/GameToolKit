using System.IO;

namespace UnityToolKit.Runtime.Common
{
    public class UnityDirectory
    {
        private static IPlatformDirectory _platformDirectory => new DefaultDirectory();

        public bool Exists(string path)
        {
            return _platformDirectory.Exists(path);
        }

        public void CreateDirectory(string path)
        {
            _platformDirectory.CreateDirectory(path);
        }

        public void Delete(string path)
        {
            _platformDirectory.Delete(path);
        }

        public string[] GetFiles(string path)
        {
            return _platformDirectory.GetFiles(path);
        }

        public string[] GetFiles(string path, string searchPattern)
        {
            return _platformDirectory.GetFiles(path, searchPattern);
        }

        public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
        {
            return _platformDirectory.GetFiles(path, searchPattern, searchOption);
        }

        public string[] GetDirectories(string path)
        {
            return _platformDirectory.GetDirectories(path);
        }

        public string[] GetDirectories(string path, string searchPattern)
        {
            return _platformDirectory.GetDirectories(path, searchPattern);
        }

        public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption)
        {
            return _platformDirectory.GetDirectories(path, searchPattern, searchOption);
        }
    }
}