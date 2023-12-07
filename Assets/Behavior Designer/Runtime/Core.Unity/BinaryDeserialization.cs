using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using UnityEngine;
using UnityEngine.Serialization;
using Task = BehaviorDesigner.Runtime.Tasks.Task;
using Unity.Mathematics;
using SerializeField = Newtonsoft.Json.JsonPropertyAttribute;

public static partial class BinaryDeserialization
{
    private static UnityEngine.Object IndexToUnityObject(int index, FieldSerializationData activeFieldSerializationData)
    {
        return index < 0 || index >= activeFieldSerializationData.unityObjects.Count
            ? null
            : activeFieldSerializationData.unityObjects[index];
    }

    private static object LoadField(
        FieldSerializationData fieldSerializationData, Dictionary<int, int> fieldIndexMap, System.Type fieldType, string fieldName, int hashPrefix, IVariableSource variableSource, object obj = null, FieldInfo fieldInfo = null)
    {
        int num1 = hashPrefix;
        int num2 = !BinaryDeserialization.shaHashSerialization
            ? num1 + (fieldType.Name.GetHashCode() + fieldName.GetHashCode())
            : num1
              + (
                  BinaryDeserialization.StringHash(fieldType.Name.ToString(),
                      BinaryDeserialization.strHashSerialization)
                  + BinaryDeserialization.StringHash(fieldName,
                      BinaryDeserialization.strHashSerialization)
              );
        int num3;
        bool flag = fieldIndexMap.TryGetValue(num2, out num3);
        if (
            !flag
            && fieldInfo != (FieldInfo)null
            && fieldInfo.GetCustomAttribute(typeof(FormerlySerializedAsAttribute), true)
                is FormerlySerializedAsAttribute customAttribute
        )
        {
            num2 =
                hashPrefix
                + BinaryDeserialization.StringHash(fieldType.Name.ToString(),
                    BinaryDeserialization.strHashSerialization)
                + BinaryDeserialization.StringHash(customAttribute.oldName,
                    BinaryDeserialization.strHashSerialization);
            flag = fieldIndexMap.TryGetValue(num2, out num3);
        }

        if (!flag)
        {
            if (fieldType.IsAbstract)
            {
                return (object)null;
            }

            if (!typeof(SharedVariable).IsAssignableFrom(fieldType))
            {
                return (object)null;
            }

            SharedVariable instance = TaskUtility.CreateInstance(fieldType) as SharedVariable;
            if (fieldInfo.GetValue(obj) is SharedVariable sharedVariable)
            {
                instance.SetValue(sharedVariable.GetValue());
            }

            return (object)instance;
        }

        object obj1 = (object)null;
        if (typeof(IList).IsAssignableFrom(fieldType))
        {
            int length = BinaryDeserialization.BytesToInt(fieldSerializationData.byteDataArray,
                fieldSerializationData.dataPosition[num3]);
            if (fieldType.IsArray)
            {
                System.Type elementType = fieldType.GetElementType();
                if (elementType == (System.Type)null)
                {
                    return (object)null;
                }

                Array instance = Array.CreateInstance(elementType, length);
                for (int index = 0; index < length; ++index)
                {
                    object objA = BinaryDeserialization.LoadField(fieldSerializationData,
                        fieldIndexMap,
                        elementType,
                        index.ToString(),
                        num2 / (!BinaryDeserialization.updatedSerialization ? 1 : index + 1),
                        variableSource,
                        obj,
                        fieldInfo);
                    instance.SetValue(object.ReferenceEquals(objA, (object)null) || objA.Equals((object)null)
                            ? (object)null
                            : objA,
                        index);
                }

                obj1 = (object)instance;
            }
            else
            {
                System.Type type = fieldType;
                while (!type.IsGenericType)
                {
                    type = type.BaseType;
                }

                System.Type genericArgument = type.GetGenericArguments()[0];
                IList instance;
                if (fieldType.IsGenericType)
                {
                    instance =
                        TaskUtility.CreateInstance(typeof(List<>).MakeGenericType(genericArgument))
                            as IList;
                }
                else
                {
                    instance = TaskUtility.CreateInstance(fieldType) as IList;
                }

                for (int index = 0; index < length; ++index)
                {
                    object objA = BinaryDeserialization.LoadField(fieldSerializationData,
                        fieldIndexMap,
                        genericArgument,
                        index.ToString(),
                        num2 / (!BinaryDeserialization.updatedSerialization ? 1 : index + 1),
                        variableSource,
                        obj,
                        fieldInfo);
                    instance.Add(object.ReferenceEquals(objA, (object)null) || objA.Equals((object)null)
                        ? (object)null
                        : objA);
                }

                obj1 = (object)instance;
            }
        }
        else if (typeof(Task).IsAssignableFrom(fieldType))
        {
            if (
                fieldInfo != (FieldInfo)null
                && TaskUtility.HasAttribute(fieldInfo, typeof(InspectTaskAttribute))
            )
            {
                string typeName = BinaryDeserialization.BytesToString(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3],
                    BinaryDeserialization.GetFieldSize(fieldSerializationData, num3));
                if (!string.IsNullOrEmpty(typeName))
                {
                    System.Type typeWithinAssembly = TaskUtility.GetTypeWithinAssembly(typeName);
                    if (typeWithinAssembly != (System.Type)null)
                    {
                        obj1 = TaskUtility.CreateInstance(typeWithinAssembly);
                        BinaryDeserialization.LoadFields(fieldSerializationData,
                            fieldIndexMap,
                            obj1,
                            num2,
                            variableSource);
                    }
                }
            }
            else
            {
                if (BinaryDeserialization.taskIDs == null)
                {
                    BinaryDeserialization.taskIDs = new Dictionary<
                        BinaryDeserialization.ObjectFieldMap,
                        List<int>
                    >((IEqualityComparer<BinaryDeserialization.ObjectFieldMap>)
                        new BinaryDeserialization.ObjectFieldMapComparer());
                }

                int num4 = BinaryDeserialization.BytesToInt(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
                BinaryDeserialization.ObjectFieldMap key = new BinaryDeserialization.ObjectFieldMap(obj,
                    fieldInfo);
                if (BinaryDeserialization.taskIDs.ContainsKey(key))
                {
                    BinaryDeserialization.taskIDs[key].Add(num4);
                }
                else
                {
                    BinaryDeserialization.taskIDs.Add(key, new List<int>() { num4 });
                }
            }
        }
        else if (typeof(SharedVariable).IsAssignableFrom(fieldType))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToSharedVariable(fieldSerializationData,
                    fieldIndexMap,
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3],
                    variableSource,
                    true,
                    num2);
        }
        else if (typeof(UnityEngine.Object).IsAssignableFrom(fieldType))
        {
            obj1 = (object)
                BinaryDeserialization.IndexToUnityObject(BinaryDeserialization.BytesToInt(fieldSerializationData.byteDataArray,
                        fieldSerializationData.dataPosition[num3]),
                    fieldSerializationData);
        }
        else if (
            fieldType.Equals(typeof(int))
            || !BinaryDeserialization.enumSerialization && fieldType.IsEnum
        )
        {
            obj1 = (object)
                BinaryDeserialization.BytesToInt(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
            if (fieldType.IsEnum)
            {
                obj1 = Enum.ToObject(fieldType, obj1);
            }
        }
        else if (fieldType.IsEnum)
        {
            obj1 = Enum.ToObject(fieldType,
                BinaryDeserialization.LoadField(fieldSerializationData,
                    fieldIndexMap,
                    Enum.GetUnderlyingType(fieldType),
                    fieldName,
                    num2,
                    variableSource,
                    obj,
                    fieldInfo));
        }
        else if (fieldType.Equals(typeof(uint)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToUInt(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(ulong)) || fieldType.Equals(typeof(ulong)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToULong(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(ushort)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToUShort(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(float)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToFloat(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(double)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToDouble(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(long)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToLong(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(ulong)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToULong(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(bool)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToBool(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(string)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToString(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3],
                    BinaryDeserialization.GetFieldSize(fieldSerializationData, num3));
        }
        else if (fieldType.Equals(typeof(byte)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToByte(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(float2)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToVector2(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(Vector2Int)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToVector2Int(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(float3)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToVector3(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(Vector3Int)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToVector3Int(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(float4)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToVector4(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(quaternion)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToQuaternion(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(Color)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToColor(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(Rect)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToRect(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(Matrix4x4)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToMatrix4x4(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(AnimationCurve)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToAnimationCurve(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.Equals(typeof(LayerMask)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToLayerMask(fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]);
        }
        else if (fieldType.IsClass || fieldType.IsValueType && !fieldType.IsPrimitive)
        {
            object instance = TaskUtility.CreateInstance(fieldType);
            BinaryDeserialization.LoadFields(fieldSerializationData,
                fieldIndexMap,
                instance,
                num2,
                variableSource);
            return instance;
        }

        return obj1;
    }


    private static void LoadFields(FieldSerializationData fieldSerializationData, Dictionary<int, int> fieldIndexMap, object obj, int hashPrefix, IVariableSource variableSource)
    {
        FieldInfo[] serializableFields = TaskUtility.GetSerializableFields(obj.GetType());
        for (int index = 0; index < serializableFields.Length; ++index)
        {
            if (!TaskUtility.HasAttribute(serializableFields[index], typeof(NonSerializedAttribute))
                && (!serializableFields[index].IsPrivate && !serializableFields[index].IsFamily
                    || TaskUtility.HasAttribute(serializableFields[index], typeof(SerializeField)))
                && (!(obj is ParentTask) || !serializableFields[index].Name.Equals("children")))
            {
                object objA = BinaryDeserialization.LoadField(fieldSerializationData,
                    fieldIndexMap,
                    serializableFields[index].FieldType,
                    serializableFields[index].Name,
                    hashPrefix,
                    variableSource,
                    obj,
                    serializableFields[index]);
                if (
                    objA != null
                    && !object.ReferenceEquals(objA, (object)null)
                    && !objA.Equals((object)null)
                    && serializableFields[index].FieldType.IsAssignableFrom(objA.GetType())
                )
                {
                    serializableFields[index].SetValue(obj, objA);
                }
            }
        }
    }

    private static Color BytesToColor(byte[] bytes, int dataPosition)
    {
        return new Color()
        {
            r = BinaryDeserialization.BytesToFloat(bytes, dataPosition),
            g = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 4),
            b = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 8),
            a = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 12)
        };
    }

    private static Vector2Int BytesToVector2Int(byte[] bytes, int dataPosition)
    {
        return new Vector2Int()
        {
            x = BinaryDeserialization.BytesToInt(bytes, dataPosition),
            y = BinaryDeserialization.BytesToInt(bytes, dataPosition + 4)
        };
    }

    private static Vector3Int BytesToVector3Int(byte[] bytes, int dataPosition)
    {
        return new Vector3Int()
        {
            x = BinaryDeserialization.BytesToInt(bytes, dataPosition),
            y = BinaryDeserialization.BytesToInt(bytes, dataPosition + 4),
            z = BinaryDeserialization.BytesToInt(bytes, dataPosition + 8)
        };
    }

    private static Rect BytesToRect(byte[] bytes, int dataPosition)
    {
        return new Rect()
        {
            x = BinaryDeserialization.BytesToFloat(bytes, dataPosition),
            y = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 4),
            width = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 8),
            height = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 12)
        };
    }

    private static AnimationCurve BytesToAnimationCurve(byte[] bytes, int dataPosition)
    {
        AnimationCurve animationCurve = new AnimationCurve();
        int num = BinaryDeserialization.BytesToInt(bytes, dataPosition);
        for (int index = 0; index < num; ++index)
        {
            animationCurve.AddKey(new Keyframe()
            {
                time = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 4),
                value = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 8),
                inTangent = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 12),
                outTangent = BitConverter.ToSingle(bytes, dataPosition + 16)
            });
            dataPosition += BinaryDeserialization.animationCurveAdvance;
        }

        animationCurve.preWrapMode = (WrapMode)
            BinaryDeserialization.BytesToInt(bytes, dataPosition + 4);
        animationCurve.postWrapMode = (WrapMode)
            BinaryDeserialization.BytesToInt(bytes, dataPosition + 8);
        return animationCurve;
    }

    private static LayerMask BytesToLayerMask(byte[] bytes, int dataPosition)
    {
        return new LayerMask() { value = BinaryDeserialization.BytesToInt(bytes, dataPosition) };
    }

    private static SharedVariable BytesToSharedVariable(
        FieldSerializationData fieldSerializationData, Dictionary<int, int> fieldIndexMap, byte[] bytes, int dataPosition, IVariableSource variableSource, bool fromField, int hashPrefix)
    {
        SharedVariable sharedVariable = (SharedVariable)null;
        string typeName =
            BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(string), "Type", hashPrefix, (IVariableSource)null) as string;
        if (string.IsNullOrEmpty(typeName))
        {
            return (SharedVariable)null;
        }

        string name =
            BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(string), "Name", hashPrefix, (IVariableSource)null) as string;
        bool boolean1 = Convert.ToBoolean(BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(bool), "IsShared", hashPrefix, (IVariableSource)null));
        bool boolean2 = Convert.ToBoolean(BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(bool), "IsGlobal", hashPrefix, (IVariableSource)null));
        bool boolean3 = Convert.ToBoolean(BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(bool), "IsDynamic", hashPrefix, (IVariableSource)null));
        if (boolean1 && (!boolean3 || BehaviorManager.IsPlaying) && fromField)
        {
            if (!boolean2)
            {
                sharedVariable = variableSource.GetVariable(name);
            }
            else
            {
                if (BinaryDeserialization.globalVariables == null)
                {
                    BinaryDeserialization.globalVariables = GlobalVariables.Instance;
                }

                if (BinaryDeserialization.globalVariables != null)
                {
                    sharedVariable = BinaryDeserialization.globalVariables.GetVariable(name);
                }
            }
        }

        System.Type typeWithinAssembly = TaskUtility.GetTypeWithinAssembly(typeName);
        if (typeWithinAssembly == (System.Type)null)
        {
            return (SharedVariable)null;
        }

        bool flag = true;
        if (sharedVariable == null || !(flag = sharedVariable.GetType().Equals(typeWithinAssembly)))
        {
            sharedVariable = TaskUtility.CreateInstance(typeWithinAssembly) as SharedVariable;
            sharedVariable.Name = name;
            sharedVariable.IsShared = boolean1;
            sharedVariable.IsGlobal = boolean2;
            sharedVariable.IsDynamic = boolean3;
            sharedVariable.Tooltip =
                BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(string), "Tooltip", hashPrefix, (IVariableSource)null) as string;
            if (!boolean2)
            {
                sharedVariable.PropertyMapping =
                    BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(string), "PropertyMapping", hashPrefix, (IVariableSource)null) as string;
                sharedVariable.PropertyMappingOwner =
                    BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(GameObject), "PropertyMappingOwner", hashPrefix, (IVariableSource)null) as GameObject;
                sharedVariable.InitializePropertyMapping(variableSource as BehaviorSource);
            }

            if (!flag)
            {
                sharedVariable.IsShared = true;
            }

            if (boolean3 && BehaviorManager.IsPlaying)
            {
                variableSource.SetVariable(name, sharedVariable);
            }

            BinaryDeserialization.LoadFields(fieldSerializationData, fieldIndexMap, (object)sharedVariable, hashPrefix, variableSource);
        }

        return sharedVariable;
    }
}