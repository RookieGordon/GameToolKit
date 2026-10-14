namespace UnityToolKit.Runtime.Common
{
    public class UnityPath
    {
        private static IPlatformPath _platformPath
        {
            get
            {
#if UNITY_EDITOR
                return new EditorPath();
#elif UNITY_STANDALONE
                return new StandalonePath();
#elif UNITY_ANDROID || UNITY_IOS
                return new MobilePath(); 
#else
                return new MobilePath(); 
#endif
            }
        }
        
        public static string PersistentDataPath()
        {
            return _platformPath.PersistentDataPath();
        }
    }
}