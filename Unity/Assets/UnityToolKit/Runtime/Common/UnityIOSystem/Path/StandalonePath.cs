using System.IO;
using UnityEngine;

namespace UnityToolKit.Runtime.Common
{
    public class StandalonePath: IPlatformPath
    {
        public string PersistentDataPath()
        {
            var path = Path.Combine(Application.dataPath, "../", "UserData");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }
    }
}