using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ToolKit.Tools.Common;

namespace ToolKit.Tools.Extension
{
    public static class ReflectionExtension
    {
        public static bool IsStatic(this Type type) => type.IsAbstract && type.IsSealed;

        public static T DeepCopyInstance<T>(this T src) where T : class
        {
            var dst = Activator.CreateInstance<T>();
            foreach (var fieldInfo in src.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                fieldInfo.SetValue(dst, fieldInfo.GetValue(src));
            return dst;
        }

        public static T GetDefaultData<T>(string srFieldName = "kDefault") where T : struct =>
            (T)typeof(T).GetField(srFieldName, BindingFlags.Static | BindingFlags.Public)?.GetValue(null);

        public static void CopyFields<T>(T src, T dst) where T : class
        {
            foreach (var fieldInfo in src.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                fieldInfo.SetValue(dst, fieldInfo.GetValue(src));
        }

        public static void CopyPublicMembers<T>(T to, T from)
        {
            var type = typeof(T);
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
                field.SetValue(to, field.GetValue(from));

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var property in properties.Collect(p => p.CanWrite && p.CanRead))
                property.SetValue(to, property.GetValue(from));
        }

        public static T CreateInstance<T>(Type type, params object[] args) =>
            (T)Activator.CreateInstance(type, args);

        public static void TraversalAllInheritedClasses<T>(Action<Type> onEachClass)
        {
            Type[] allTypes = Assembly.GetCallingAssembly().GetTypes();
            Type parentType = typeof(T);
            for (int i = 0; i < allTypes.Length; i++)
            {
                if (allTypes[i].IsClass && !allTypes[i].IsAbstract && allTypes[i].IsSubclassOf(parentType))
                    onEachClass(allTypes[i]);
            }
        }

        public static void TraversalAllInheritedClasses<T>(Action<Type, T> OnInstanceCreated,
            params object[] constructorArgs) => TraversalAllInheritedClasses<T>(type =>
            OnInstanceCreated(type, CreateInstance<T>(type, constructorArgs)));

        public static void InvokeAllMethod<T>(List<Type> classes, string methodName, T template) where T : class
        {
            foreach (Type t in classes)
            {
                MethodInfo method = t.GetMethod(methodName);
                if (method == null)
                    throw new Exception("Null Method Found From:" + t.ToString() + "." + methodName);
                method.Invoke(null, new object[] { template });
            }
        }

        public static Stack<Type> GetInheritTypes(this Type type)
        {
            if (type == null)
                throw new NullReferenceException();

            Stack<Type> inheritStack = new Stack<Type>();
            while (type.BaseType != null)
            {
                type = type.BaseType;
                inheritStack.Push(type);
            }

            return inheritStack;
        }

        public static IEnumerable<Type> GetChildTypes(this Type baseType, bool @abstract = false)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes()
                             .Where(_type => (@abstract || !_type.IsAbstract) && _type.IsSubclassOf(baseType)))
                {
                    yield return type;
                }
            }
        }

        public static object GetValue(this Stack<FieldInfo> fieldStacks, object targetObject)
        {
            object dstObject = targetObject;
            foreach (var field in fieldStacks)
                dstObject = field.GetValue(dstObject);
            return dstObject;
        }

        public static T GetFieldValue<T>(object targetObject, string fieldName,
            BindingFlags _flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        {
            if (targetObject == null)
                return default;

            var objectType = targetObject.GetType();
            var targetType = typeof(T);
            var field = objectType.GetField(fieldName, _flags);
            if (field.FieldType != targetType)
            {
                Log.Error($"Invalid Field {targetType.Name} From:{targetType.Name} ");
                return default;
            }

            return (T)field.GetValue(targetObject);
        }

        public static void SetFieldValue<T>(object targetObject, string fieldName, T value,
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        {
            if (targetObject == null)
                return;

            var objectType = targetObject.GetType();
            var targetType = typeof(T);
            var field = objectType.GetField(fieldName, flags);
            if (field.FieldType != targetType)
            {
                Log.Error($"Invalid Field {field.FieldType} From:{targetType.Name} ");
                return;
            }

            field.SetValue(targetObject, value);
        }

        public static void SetValue(this Stack<FieldInfo> fieldStacks, object targetObject, object value)
        {
            Stack<object> dstObjects = new Stack<object>();
            object dstObject = targetObject;
            int totalCount = fieldStacks.Count;
            int fieldCount = totalCount;
            foreach (var field in fieldStacks)
            {
                if (--fieldCount == 0)
                    break;
                dstObject = field.GetValue(dstObject);
                dstObjects.Push(dstObject);
            }

            dstObject = value;
            for (int i = totalCount - 1; i >= 0; i--)
            {
                FieldInfo field = fieldStacks.ElementAt(i);
                object tarObject = dstObjects.Count == 0 ? targetObject : dstObjects.Pop();
                field.SetValue(tarObject, dstObject);
                dstObject = tarObject;
            }
        }

        public static IEnumerable<FieldInfo> GetAllFields(this Type _type, BindingFlags flags = BindingFlags.Instance)
        {
            if (_type == null)
                throw new NullReferenceException();

            foreach (var fieldInfo in _type.GetFields(flags | BindingFlags.Public | BindingFlags.NonPublic))
                yield return fieldInfo;
            var inheritStack = _type.GetInheritTypes();
            while (inheritStack.Count > 0)
            {
                var type = inheritStack.Pop();
                foreach (var fieldInfo in type.GetFields(flags | BindingFlags.NonPublic))
                    if (fieldInfo.IsPrivate)
                        yield return fieldInfo;
            }
        }

        public static IEnumerable<MethodInfo> GetAllMethods(this Type _type, BindingFlags flags)
        {
            if (_type == null)
                throw new NullReferenceException();

            foreach (var methodInfo in _type.GetMethods(flags | BindingFlags.Public | BindingFlags.NonPublic))
                yield return methodInfo;
            var inheritStack = _type.GetInheritTypes();
            while (inheritStack.Count > 0)
            {
                var type = inheritStack.Pop();
                foreach (var methodInfo in type.GetMethods(flags | BindingFlags.NonPublic))
                    if (methodInfo.IsPrivate)
                        yield return methodInfo;
            }
        }
    }
}