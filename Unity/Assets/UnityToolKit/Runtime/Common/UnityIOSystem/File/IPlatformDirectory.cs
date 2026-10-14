namespace UnityToolKit.Runtime.Common
{
    public interface IPlatformDirectory
    {
        bool Exists(string path);
        void CreateDirectory(string path);
        void Delete(string path);
        string[] GetFiles(string path);
        string[] GetFiles(string path, string searchPattern);
        string[] GetFiles(string path, string searchPattern, System.IO.SearchOption searchOption);
        string[] GetDirectories(string path);
        string[] GetDirectories(string path, string searchPattern);
        string[] GetDirectories(string path, string searchPattern, System.IO.SearchOption searchOption);
    }
}