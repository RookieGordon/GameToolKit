using UnityEngine;

namespace UnityToolKit.Runtime.Common
{
    public class MobilePath: IPlatformPath
    {
        public string PersistentDataPath()
        {
            return Application.persistentDataPath;
        }
    }
}