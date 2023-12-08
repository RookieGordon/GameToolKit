using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BehaviorDesigner.Runtime.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using Task = BehaviorDesigner.Runtime.Tasks.Task;

namespace BehaviorDesigner.Runtime
{
    public partial class JSONDeserialization : UnityEngine.Object
    {
        private static SharedVariable DeserializeSharedVariable(
            Dictionary<string, object> dict,
            IVariableSource variableSource,
            bool fromSource,
            List<UnityEngine.Object> unityObjects
        )
        {
            if (dict == null)
            {
                return null;
            }

            SharedVariable sharedVariable = (SharedVariable)null;
            object name;
            if (
                !fromSource
                && variableSource != null
                && dict.TryGetValue("Name", out name)
                && (BehaviorManager.IsPlaying || !dict.ContainsKey("IsDynamic"))
            )
            {
                object obj;
                dict.TryGetValue("IsGlobal", out obj);
                if (
                    !dict.TryGetValue("IsGlobal", out obj)
                    || !Convert.ToBoolean(obj, (IFormatProvider)CultureInfo.InvariantCulture)
                )
                {
                    sharedVariable = variableSource.GetVariable(name as string);
                }
                else
                {
                    if (JSONDeserialization.globalVariables == null)
                    {
                        JSONDeserialization.globalVariables = GlobalVariables.Instance;
                    }

                    if (JSONDeserialization.globalVariables != null)
                    {
                        sharedVariable = JSONDeserialization
                            .globalVariables
                            .GetVariable(name as string);
                    }
                }
            }

            System.Type typeWithinAssembly = TaskUtility.GetTypeWithinAssembly(dict["Type"] as string);
            if (typeWithinAssembly == (System.Type)null)
                return (SharedVariable)null;
            bool flag = true;
            if (
                sharedVariable == null
                || !(flag = sharedVariable.GetType().Equals(typeWithinAssembly))
            )
            {
                sharedVariable = TaskUtility.CreateInstance(typeWithinAssembly) as SharedVariable;
                sharedVariable.Name = dict["Name"] as string;
                object obj;
                if (dict.TryGetValue("IsShared", out obj))
                    sharedVariable.IsShared = Convert.ToBoolean(obj,
                        (IFormatProvider)CultureInfo.InvariantCulture);
                if (dict.TryGetValue("IsGlobal", out obj))
                    sharedVariable.IsGlobal = Convert.ToBoolean(obj,
                        (IFormatProvider)CultureInfo.InvariantCulture);
                if (dict.TryGetValue("IsDynamic", out obj))
                {
                    sharedVariable.IsDynamic = Convert.ToBoolean(obj,
                        (IFormatProvider)CultureInfo.InvariantCulture);
                    if (BehaviorManager.IsPlaying)
                        variableSource.SetVariable(sharedVariable.Name, sharedVariable);
                }

                if (dict.TryGetValue("Tooltip", out obj))
                    sharedVariable.Tooltip = obj as string;
                if (!sharedVariable.IsGlobal && dict.TryGetValue("PropertyMapping", out obj))
                {
                    sharedVariable.PropertyMapping = obj as string;
                    if (dict.TryGetValue("PropertyMappingOwner", out obj))
                        sharedVariable.PropertyMappingOwner =
                            JSONDeserialization.IndexToUnityObject(Convert.ToInt32(obj, (IFormatProvider)CultureInfo.InvariantCulture),
                                unityObjects) as GameObject;
                    sharedVariable.InitializePropertyMapping(variableSource as BehaviorSource);
                }

                if (!flag)
                    sharedVariable.IsShared = true;
                JSONDeserialization.DeserializeObject((Task)null,
                    (object)sharedVariable,
                    dict,
                    variableSource,
                    unityObjects);
            }

            return sharedVariable;
        }

        private static void DeserializeVariables(
            IVariableSource variableSource,
            Dictionary<string, object> dict,
            List<UnityEngine.Object> unityObjects
        )
        {
            if (!dict.TryGetValue("Variables", out var obj))
            {
                return;
            }

            List<SharedVariable> variables = new List<SharedVariable>();
            IList list = obj as IList;
            for (int index = 0; index < list.Count; ++index)
            {
                SharedVariable sharedVariable = JSONDeserialization.DeserializeSharedVariable(list[index] as Dictionary<string, object>,
                    variableSource,
                    true,
                    unityObjects);
                variables.Add(sharedVariable);
            }

            variableSource.SetAllVariables(variables);
        }

        private static void DeserializeObject(
            Task task,
            object obj,
            Dictionary<string, object> dict,
            IVariableSource variableSource,
            List<UnityEngine.Object> unityObjects
        )
        {
            if (dict == null || obj == null)
                return;
            FieldInfo[] serializableFields = TaskUtility.GetSerializableFields(obj.GetType());
            for (int index1 = 0; index1 < serializableFields.Length; ++index1)
            {
                object obj1 = (object)null;
                string key = !JSONDeserialization.updatedSerialization
                    ? (
                        serializableFields[index1].FieldType.Name.GetHashCode()
                        + serializableFields[index1].Name.GetHashCode()
                    ).ToString()
                    : serializableFields[index1].FieldType.Name + serializableFields[index1].Name;
                if (
                    !dict.TryGetValue(key, out obj1)
                    && serializableFields[index1].GetCustomAttribute(typeof(FormerlySerializedAsAttribute),
                            true)
                        is FormerlySerializedAsAttribute customAttribute
                )
                    dict.TryGetValue(serializableFields[index1].FieldType.Name + customAttribute.oldName,
                        out obj1);
                if (obj1 != null)
                {
                    if (typeof(IList).IsAssignableFrom(serializableFields[index1].FieldType))
                    {
                        if (obj1 is IList list)
                        {
                            System.Type elementType;
                            if (serializableFields[index1].FieldType.IsArray)
                            {
                                elementType = serializableFields[index1].FieldType.GetElementType();
                            }
                            else
                            {
                                System.Type type = serializableFields[index1].FieldType;
                                while (!type.IsGenericType)
                                    type = type.BaseType;
                                elementType = type.GetGenericArguments()[0];
                            }

                            if (
                                (
                                    elementType.Equals(typeof(Task))
                                    || elementType.IsSubclassOf(typeof(Task))
                                )
                                && !TaskUtility.HasAttribute(serializableFields[index1],
                                    typeof(InspectTaskAttribute))
                            )
                            {
                                if (JSONDeserialization.taskIDs != null)
                                {
                                    List<int> intList = new List<int>();
                                    for (int index2 = 0; index2 < list.Count; ++index2)
                                        intList.Add(Convert.ToInt32(list[index2],
                                            (IFormatProvider)CultureInfo.InvariantCulture));
                                    JSONDeserialization
                                        .taskIDs
                                        .Add(new JSONDeserialization.TaskField(task,
                                                serializableFields[index1]),
                                            intList);
                                }
                            }
                            else if (serializableFields[index1].FieldType.IsArray)
                            {
                                Array instance = Array.CreateInstance(elementType, list.Count);
                                for (int index3 = 0; index3 < list.Count; ++index3)
                                {
                                    if (list[index3] == null)
                                    {
                                        instance.SetValue((object)null, index3);
                                    }
                                    else
                                    {
                                        System.Type type;
                                        object obj2;
                                        if (list[index3] is Dictionary<string, object>)
                                        {
                                            Dictionary<string, object> dictionary =
                                                (Dictionary<string, object>)list[index3];
                                            type = TaskUtility.GetTypeWithinAssembly((string)dictionary["Type"]);
                                            if (!dictionary.TryGetValue("Value", out obj2))
                                                obj2 = list[index3];
                                        }
                                        else
                                        {
                                            type = elementType;
                                            obj2 = list[index3];
                                        }

                                        object o = JSONDeserialization.ValueToObject(task,
                                            type,
                                            obj2,
                                            variableSource,
                                            unityObjects);
                                        if (!elementType.IsInstanceOfType(o))
                                            instance.SetValue((object)null, index3);
                                        else
                                            instance.SetValue(o, index3);
                                    }
                                }

                                serializableFields[index1].SetValue(obj, (object)instance);
                            }
                            else
                            {
                                IList instance;
                                if (serializableFields[index1].FieldType.IsGenericType)
                                    instance =
                                        TaskUtility.CreateInstance(typeof(List<>).MakeGenericType(elementType)) as IList;
                                else
                                    instance =
                                        TaskUtility.CreateInstance(serializableFields[index1].FieldType) as IList;
                                for (int index4 = 0; index4 < list.Count; ++index4)
                                {
                                    if (list[index4] == null)
                                    {
                                        instance.Add((object)null);
                                    }
                                    else
                                    {
                                        System.Type type = elementType;
                                        object obj3 = list[index4];
                                        if (obj3 is Dictionary<string, object>)
                                        {
                                            Dictionary<string, object> dictionary =
                                                (Dictionary<string, object>)obj3;
                                            object typeName;
                                            if (dictionary.TryGetValue("Type", out typeName))
                                            {
                                                type = TaskUtility.GetTypeWithinAssembly((string)typeName);
                                                if (!dictionary.TryGetValue("Value", out obj3))
                                                    obj3 = list[index4];
                                            }
                                        }

                                        object obj4 = JSONDeserialization.ValueToObject(task,
                                            type,
                                            obj3,
                                            variableSource,
                                            unityObjects);
                                        if (obj4 != null && !obj4.Equals((object)null))
                                            instance.Add(obj4);
                                        else
                                            instance.Add((object)null);
                                    }
                                }

                                serializableFields[index1].SetValue(obj, (object)instance);
                            }
                        }
                    }
                    else
                    {
                        System.Type fieldType = serializableFields[index1].FieldType;
                        if (fieldType.Equals(typeof(Task)) || fieldType.IsSubclassOf(typeof(Task)))
                        {
                            if (
                                TaskUtility.HasAttribute(serializableFields[index1],
                                    typeof(InspectTaskAttribute))
                            )
                            {
                                Dictionary<string, object> dict1 =
                                    obj1 as Dictionary<string, object>;
                                System.Type typeWithinAssembly = TaskUtility.GetTypeWithinAssembly(dict1["Type"] as string);
                                if (typeWithinAssembly != (System.Type)null)
                                {
                                    Task instance =
                                        TaskUtility.CreateInstance(typeWithinAssembly) as Task;
                                    JSONDeserialization.DeserializeObject(instance,
                                        (object)instance,
                                        dict1,
                                        variableSource,
                                        unityObjects);
                                    serializableFields[index1].SetValue((object)task,
                                        (object)instance);
                                }
                            }
                            else if (JSONDeserialization.taskIDs != null)
                                JSONDeserialization
                                    .taskIDs
                                    .Add(new JSONDeserialization.TaskField(task,
                                            serializableFields[index1]),
                                        new List<int>()
                                        {
                                            Convert.ToInt32(obj1,
                                                (IFormatProvider)CultureInfo.InvariantCulture)
                                        });
                        }
                        else
                        {
                            object obj5 = JSONDeserialization.ValueToObject(task,
                                fieldType,
                                obj1,
                                variableSource,
                                unityObjects);
                            if (
                                obj5 != null
                                && !obj5.Equals((object)null)
                                && fieldType.IsAssignableFrom(obj5.GetType())
                            )
                                serializableFields[index1].SetValue(obj, obj5);
                        }
                    }
                }
                else if (
                    typeof(SharedVariable).IsAssignableFrom(serializableFields[index1].FieldType)
                    && !serializableFields[index1].FieldType.IsAbstract
                )
                {
                    if (
                        dict.TryGetValue((
                                serializableFields[index1].FieldType.Name.GetHashCode()
                                + serializableFields[index1].Name.GetHashCode()
                            ).ToString(),
                            out obj1)
                    )
                    {
                        SharedVariable instance =
                            TaskUtility.CreateInstance(serializableFields[index1].FieldType)
                                as SharedVariable;
                        instance.SetValue(JSONDeserialization.ValueToObject(task,
                            serializableFields[index1].FieldType,
                            obj1,
                            variableSource,
                            unityObjects));
                        serializableFields[index1].SetValue(obj, (object)instance);
                    }
                    else
                    {
                        SharedVariable instance =
                            TaskUtility.CreateInstance(serializableFields[index1].FieldType)
                                as SharedVariable;
                        if (
                            serializableFields[index1].GetValue(obj)
                            is SharedVariable sharedVariable
                        )
                            instance.SetValue(sharedVariable.GetValue());
                        serializableFields[index1].SetValue(obj, (object)instance);
                    }
                }
            }
        }

        public static Task DeserializeTask(
            BehaviorSource behaviorSource,
            Dictionary<string, object> dict,
            ref Dictionary<int, Task> IDtoTask,
            List<UnityEngine.Object> unityObjects
        )
        {
            Task task = (Task)null;
            try
            {
                System.Type t = TaskUtility.GetTypeWithinAssembly(dict["Type"] as string);
                if (t == (System.Type)null)
                    t = !dict.ContainsKey("Children")
                        ? typeof(UnknownTask)
                        : typeof(UnknownParentTask);
                task = TaskUtility.CreateInstance(t) as Task;
                if (task is UnknownTask)
                {
                    (task as UnknownTask).JSONSerialization = MiniJSON.Serialize((object)dict);
                }
            }
            catch (Exception ex)
            {
            }

            if (task == null)
            {
                return null;
            }

            task.Owner = behaviorSource.Owner.GetObject() as Behavior;
            task.ID = Convert.ToInt32(dict["ID"], (IFormatProvider)CultureInfo.InvariantCulture);
            object obj;
            if (dict.TryGetValue("Name", out obj))
                task.FriendlyName = (string)obj;
            if (dict.TryGetValue("Instant", out obj))
            {
                task.IsInstant = Convert.ToBoolean(obj,
                    (IFormatProvider)CultureInfo.InvariantCulture);
            }

            if (dict.TryGetValue("Disabled", out obj))
            {
                task.Disabled = Convert.ToBoolean(obj,
                    (IFormatProvider)CultureInfo.InvariantCulture);
            }

            IDtoTask.Add(task.ID, task);
            task.NodeData = JSONDeserialization.DeserializeNodeData(dict["NodeData"] as Dictionary<string, object>,
                task);
            if (
                task.GetType().Equals(typeof(UnknownTask))
                || task.GetType().Equals(typeof(UnknownParentTask))
            )
            {
                if (!task.FriendlyName.Contains("Unknown "))
                    task.FriendlyName = string.Format("Unknown {0}", (object)task.FriendlyName);
                task.NodeData.Comment = "Unknown Task. Right click and Replace to locate new task.";
            }

            JSONDeserialization.DeserializeObject(task,
                (object)task,
                dict,
                (IVariableSource)behaviorSource,
                unityObjects);
            if (
                task is ParentTask
                && dict.TryGetValue("Children", out obj)
                && task is ParentTask parentTask
            )
            {
                foreach (Dictionary<string, object> dict1 in obj as IEnumerable)
                {
                    Task child = JSONDeserialization.DeserializeTask(behaviorSource,
                        dict1,
                        ref IDtoTask,
                        unityObjects);
                    int index = parentTask.Children != null ? parentTask.Children.Count : 0;
                    parentTask.AddChild(child, index);
                }
            }

            return task;
        }

        private static object ValueToObject(
            Task task,
            System.Type type,
            object obj,
            IVariableSource variableSource,
            List<UnityEngine.Object> unityObjects
        )
        {
            if (typeof(SharedVariable).IsAssignableFrom(type))
            {
                SharedVariable sharedVariable = JSONDeserialization.DeserializeSharedVariable(obj as Dictionary<string, object>,
                    variableSource,
                    false,
                    unityObjects);
                if (sharedVariable == null && !type.IsAbstract)
                    sharedVariable = TaskUtility.CreateInstance(type) as SharedVariable;
                return (object)sharedVariable;
            }

            if (
                type.Equals(typeof(UnityEngine.Object))
                || type.IsSubclassOf(typeof(UnityEngine.Object))
            )
                return (object)
                    JSONDeserialization.IndexToUnityObject(Convert.ToInt32(obj, (IFormatProvider)CultureInfo.InvariantCulture),
                        unityObjects);
            if (!type.IsPrimitive)
            {
                if (!type.Equals(typeof(string)))
                {
                    if (type.IsSubclassOf(typeof(Enum)))
                    {
                        try
                        {
                            return Enum.Parse(type, (string)obj);
                        }
                        catch (Exception ex)
                        {
                            return (object)null;
                        }
                    }
                    else
                    {
                        if (type.Equals(typeof(float2)))
                            return (object)JSONDeserialization.StringToVector2((string)obj);
                        if (type.Equals(typeof(Vector2Int)))
                            return (object)JSONDeserialization.StringToVector2Int((string)obj);
                        if (type.Equals(typeof(float3)))
                            return (object)JSONDeserialization.StringToVector3((string)obj);
                        if (type.Equals(typeof(Vector3Int)))
                            return (object)JSONDeserialization.StringToVector3Int((string)obj);
                        if (type.Equals(typeof(float4)))
                            return (object)JSONDeserialization.StringToVector4((string)obj);
                        if (type.Equals(typeof(quaternion)))
                            return (object)JSONDeserialization.StringToQuaternion((string)obj);
                        if (type.Equals(typeof(float4x4)))
                            return (object)JSONDeserialization.StringToMatrix4x4((string)obj);
                        if (type.Equals(typeof(Color)))
                            return (object)JSONDeserialization.StringToColor((string)obj);
                        if (type.Equals(typeof(Rect)))
                            return (object)JSONDeserialization.StringToRect((string)obj);
                        if (type.Equals(typeof(LayerMask)))
                            return (object)
                                JSONDeserialization.ValueToLayerMask(Convert.ToInt32(obj,
                                    (IFormatProvider)CultureInfo.InvariantCulture));
                        if (type.Equals(typeof(AnimationCurve)))
                            return (object)
                                JSONDeserialization.ValueToAnimationCurve((Dictionary<string, object>)obj);
                        object instance = TaskUtility.CreateInstance(type);
                        JSONDeserialization.DeserializeObject(task,
                            instance,
                            obj as Dictionary<string, object>,
                            variableSource,
                            unityObjects);
                        return instance;
                    }
                }
            }

            try
            {
                return Convert.ChangeType(obj, type);
            }
            catch (Exception ex)
            {
                return (object)null;
            }
        }

        private static Vector2Int StringToVector2Int(string vector2String)
        {
            string[] strArray = vector2String.Substring(1, vector2String.Length - 2).Split(',');
            return new Vector2Int(int.Parse(strArray[0], (IFormatProvider)CultureInfo.InvariantCulture),
                int.Parse(strArray[1], (IFormatProvider)CultureInfo.InvariantCulture));
        }

        private static Vector3Int StringToVector3Int(string vector3String)
        {
            string[] strArray = vector3String.Substring(1, vector3String.Length - 2).Split(',');
            return new Vector3Int(int.Parse(strArray[0], (IFormatProvider)CultureInfo.InvariantCulture),
                int.Parse(strArray[1], (IFormatProvider)CultureInfo.InvariantCulture),
                int.Parse(strArray[2], (IFormatProvider)CultureInfo.InvariantCulture));
        }

        private static Color StringToColor(string colorString)
        {
            string[] strArray = colorString.Substring(5, colorString.Length - 6).Split(',');
            return new Color(float.Parse(strArray[0], (IFormatProvider)CultureInfo.InvariantCulture),
                float.Parse(strArray[1], (IFormatProvider)CultureInfo.InvariantCulture),
                float.Parse(strArray[2], (IFormatProvider)CultureInfo.InvariantCulture),
                float.Parse(strArray[3], (IFormatProvider)CultureInfo.InvariantCulture));
        }

        private static Rect StringToRect(string rectString)
        {
            string[] strArray = rectString.Substring(1, rectString.Length - 2).Split(',');
            return new Rect(float.Parse(strArray[0].Substring(2, strArray[0].Length - 2),
                    (IFormatProvider)CultureInfo.InvariantCulture),
                float.Parse(strArray[1].Substring(3, strArray[1].Length - 3),
                    (IFormatProvider)CultureInfo.InvariantCulture),
                float.Parse(strArray[2].Substring(7, strArray[2].Length - 7),
                    (IFormatProvider)CultureInfo.InvariantCulture),
                float.Parse(strArray[3].Substring(8, strArray[3].Length - 8),
                    (IFormatProvider)CultureInfo.InvariantCulture));
        }

        private static LayerMask ValueToLayerMask(int value) => new LayerMask() { value = value };

        private static AnimationCurve ValueToAnimationCurve(Dictionary<string, object> value)
        {
            AnimationCurve animationCurve = new AnimationCurve();
            object obj;
            if (value.TryGetValue("Keys", out obj))
            {
                List<object> objectList1 = obj as List<object>;
                for (int index = 0; index < objectList1.Count; ++index)
                {
                    List<object> objectList2 = objectList1[index] as List<object>;
                    Keyframe key = new Keyframe((float)
                        Convert.ChangeType(objectList2[0],
                            typeof(float),
                            (IFormatProvider)CultureInfo.InvariantCulture),
                        (float)
                        Convert.ChangeType(objectList2[1],
                            typeof(float),
                            (IFormatProvider)CultureInfo.InvariantCulture),
                        (float)
                        Convert.ChangeType(objectList2[2],
                            typeof(float),
                            (IFormatProvider)CultureInfo.InvariantCulture),
                        (float)
                        Convert.ChangeType(objectList2[3],
                            typeof(float),
                            (IFormatProvider)CultureInfo.InvariantCulture));
                    animationCurve.AddKey(key);
                }
            }

            if (value.TryGetValue("PreWrapMode", out obj))
                animationCurve.preWrapMode = (WrapMode)Enum.Parse(typeof(WrapMode), (string)obj);
            if (value.TryGetValue("PostWrapMode", out obj))
                animationCurve.postWrapMode = (WrapMode)Enum.Parse(typeof(WrapMode), (string)obj);
            return animationCurve;
        }

        private static UnityEngine.Object IndexToUnityObject(
            int index,
            List<UnityEngine.Object> unityObjects
        )
        {
            return index < 0 || index >= unityObjects.Count
                ? (UnityEngine.Object)null
                : unityObjects[index];
        }
    }
}