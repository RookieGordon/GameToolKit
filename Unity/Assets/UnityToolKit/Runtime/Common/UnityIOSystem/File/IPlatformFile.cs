namespace UnityToolKit.Runtime.Common
{
    public interface IPlatformFile
    {
        bool Exists(string filePath);
        byte[] ReadAllBytes(string filePath);
        string ReadAllText(string filePath);
        void WriteAllBytes(string filePath, byte[] bytes);
        void WriteAllText(string filePath, string contents);
        void Delete(string filePath);
        void Copy(string sourceFileName, string destFileName);
    }
}