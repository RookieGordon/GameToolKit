using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace BehaviorDesigner.Runtime
{
    public class IgnoreBaseClassConverter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var type = value.GetType();
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Where(prop => prop.DeclaringType != type.BaseType && prop.GetCustomAttribute<JsonIgnoreAttribute>() == null);
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                             .Where(field => field.DeclaringType != type.BaseType && field.GetCustomAttribute<JsonIgnoreAttribute>() == null);

            writer.WriteStartObject();
            foreach (var prop in props)
            {
                var propVal = prop.GetValue(value, null);
                var propName = prop.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName ?? prop.Name;
                writer.WritePropertyName(propName);
                serializer.Serialize(writer, propVal);
            }
            foreach (var field in fields)
            {
                var fieldVal = field.GetValue(value);
                var fieldName = field.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName ?? field.Name;
                writer.WritePropertyName(fieldName);
                serializer.Serialize(writer, fieldVal);
            }
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var instance = Activator.CreateInstance(objectType);
            var props = objectType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                  .Where(prop => prop.DeclaringType != objectType.BaseType && prop.GetCustomAttribute<JsonIgnoreAttribute>() == null);
            var fields = objectType.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                   .Where(field => field.DeclaringType != objectType.BaseType && field.GetCustomAttribute<JsonIgnoreAttribute>() == null);

            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.PropertyName)
                {
                    var propName = reader.Value.ToString();
                    if (reader.Read())
                    {
                        var prop = props.FirstOrDefault(p => p.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName == propName || p.Name == propName);
                        var field = fields.FirstOrDefault(f => f.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName == propName || f.Name == propName);
                        if (prop != null)
                        {
                            var value = serializer.Deserialize(reader, prop.PropertyType);
                            prop.SetValue(instance, value);
                        }
                        else if (field != null)
                        {
                            var value = serializer.Deserialize(reader, field.FieldType);
                            field.SetValue(instance, value);
                        }
                    }
                }
                else if (reader.TokenType == JsonToken.EndObject)
                {
                    return instance;
                }
            }

            return instance;
        }

        public override bool CanConvert(Type objectType)
        {
            return Attribute.IsDefined(objectType, typeof(JsonIgnoreBaseAttribute));
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class JsonIgnoreBaseAttribute : Attribute
    {
    }
}