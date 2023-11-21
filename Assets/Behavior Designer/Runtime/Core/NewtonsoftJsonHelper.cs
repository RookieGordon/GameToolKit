using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace BehaviorDesigner.Runtime
{
    public class NewtonsoftJsonHelper
    {
        private static JsonSerializerSettings Setting = new JsonSerializerSettings()
        {
            Converters = new List<JsonConverter> { new IgnoreBaseClassConverter() },
                Formatting = Formatting.Indented,
        };

        public static string SerializeObject(System.Object o)
        {
            return JsonConvert.SerializeObject(o, NewtonsoftJsonHelper.Setting);
        }

        public static T DeSerializeObject<T>(string jsonStr)
        {
            return JsonConvert.DeserializeObject<T>(jsonStr, NewtonsoftJsonHelper.Setting);
        }

        public static System.Object DeSerializeObject(string jsonStr, Type type)
        {
            return JsonConvert.DeserializeObject(jsonStr, type);
        }

        public static System.Object CopyObject(System.Object o)
        {
            var jsonStr = NewtonsoftJsonHelper.SerializeObject(o);
            return NewtonsoftJsonHelper.DeSerializeObject(jsonStr, o.GetType());
        }

        public static T CopyObject<T>(System.Object o)
        {
            var jsonStr = NewtonsoftJsonHelper.SerializeObject(o);
            return NewtonsoftJsonHelper.DeSerializeObject<T>(jsonStr);
        }
    }
}

