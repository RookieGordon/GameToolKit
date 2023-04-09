using System;
using Newtonsoft.Json;

namespace Bonsai.Utility
{
    public class SerializeHelper
    {
        private static JsonSerializerSettings Setting = new JsonSerializerSettings()
        {
            PreserveReferencesHandling = PreserveReferencesHandling.Objects,
            ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
            TypeNameHandling = TypeNameHandling.All,
            Formatting = Formatting.Indented,
        };
        
        public static string SerializeObject(System.Object o)
        {
            return JsonConvert.SerializeObject(o, SerializeHelper.Setting);
        }

        public static T DeSerializeObject<T>(string jsonStr)
        {
            return JsonConvert.DeserializeObject<T>(jsonStr, SerializeHelper.Setting);
        }

        public static System.Object DeSerializeObject(string jsonStr, Type type)
        {
            return JsonConvert.DeserializeObject(jsonStr, type);
        }

        public static System.Object CopyObject(System.Object o)
        {
            var jsonStr = SerializeHelper.SerializeObject(o);
            return SerializeHelper.DeSerializeObject(jsonStr, o.GetType());
        }

        public static T CopyObject<T>(System.Object o)
        {
            var jsonStr = SerializeHelper.SerializeObject(o);
            return SerializeHelper.DeSerializeObject<T>(jsonStr);
        }
    }
}