// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.JSONSerialization
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
  public class JSONSerialization : UnityEngine.Object
  {
    private static TaskSerializationData taskSerializationData;
    private static FieldSerializationData fieldSerializationData;
    private static VariableSerializationData variableSerializationData;

    public static void Save(BehaviorSource behaviorSource)
    {
      behaviorSource.CheckForSerialization(false, (BehaviorSource) null, false);
      JSONSerialization.taskSerializationData = new TaskSerializationData();
      JSONSerialization.fieldSerializationData = JSONSerialization.taskSerializationData.fieldSerializationData;
      Dictionary<string, object> dictionary = new Dictionary<string, object>();
      if (behaviorSource.EntryTask != null)
        dictionary.Add("EntryTask", (object) JSONSerialization.SerializeTask(behaviorSource.EntryTask, true, ref JSONSerialization.fieldSerializationData.unityObjects));
      if (behaviorSource.RootTask != null)
        dictionary.Add("RootTask", (object) JSONSerialization.SerializeTask(behaviorSource.RootTask, true, ref JSONSerialization.fieldSerializationData.unityObjects));
      if (behaviorSource.DetachedTasks != null && behaviorSource.DetachedTasks.Count > 0)
      {
        Dictionary<string, object>[] dictionaryArray = new Dictionary<string, object>[behaviorSource.DetachedTasks.Count];
        for (int index = 0; index < behaviorSource.DetachedTasks.Count; ++index)
          dictionaryArray[index] = JSONSerialization.SerializeTask(behaviorSource.DetachedTasks[index], true, ref JSONSerialization.fieldSerializationData.unityObjects);
        dictionary.Add("DetachedTasks", (object) dictionaryArray);
      }
      if (behaviorSource.Variables != null && behaviorSource.Variables.Count > 0)
        dictionary.Add("Variables", (object) JSONSerialization.SerializeVariables(behaviorSource.Variables, ref JSONSerialization.fieldSerializationData.unityObjects));
      JSONSerialization.taskSerializationData.Version = "1.7.7";
      JSONSerialization.taskSerializationData.JSONSerialization = MiniJSON.Serialize((object) dictionary);
      behaviorSource.TaskData = JSONSerialization.taskSerializationData;
      if (behaviorSource.Owner == null || ((object) behaviorSource.Owner).Equals((object) null))
        return;
      BehaviorDesignerUtility.SetObjectDirty(behaviorSource.Owner.GetObject());
    }

    public static void Save(GlobalVariables variables)
    {
      if ((UnityEngine.Object) variables == (UnityEngine.Object) null)
        return;
      JSONSerialization.variableSerializationData = new VariableSerializationData();
      JSONSerialization.fieldSerializationData = JSONSerialization.variableSerializationData.fieldSerializationData;
      JSONSerialization.variableSerializationData.JSONSerialization = MiniJSON.Serialize((object) new Dictionary<string, object>()
      {
        {
          "Variables",
          (object) JSONSerialization.SerializeVariables(variables.Variables, ref JSONSerialization.fieldSerializationData.unityObjects)
        }
      });
      variables.VariableData = JSONSerialization.variableSerializationData;
      variables.Version = "1.7.7";
      BehaviorDesignerUtility.SetObjectDirty((UnityEngine.Object) variables);
    }

    private static Dictionary<string, object>[] SerializeVariables(
      List<SharedVariable> variables,
      ref List<UnityEngine.Object> unityObjects)
    {
      Dictionary<string, object>[] array = new Dictionary<string, object>[variables.Count];
      int newSize = 0;
      for (int index = 0; index < variables.Count; ++index)
      {
        Dictionary<string, object> dictionary = JSONSerialization.SerializeVariable(variables[index], ref unityObjects);
        if (dictionary != null)
        {
          array[newSize] = dictionary;
          ++newSize;
        }
      }
      if (newSize != variables.Count)
        Array.Resize<Dictionary<string, object>>(ref array, newSize);
      return array;
    }

    public static Dictionary<string, object> SerializeTask(
      Task task,
      bool serializeChildren,
      ref List<UnityEngine.Object> unityObjects)
    {
      Dictionary<string, object> dict = new Dictionary<string, object>();
      dict.Add("Type", (object) ((object) task).GetType());
      dict.Add("NodeData", (object) JSONSerialization.SerializeNodeData(task.NodeData));
      dict.Add("ID", (object) task.ID);
      dict.Add("Name", (object) task.FriendlyName);
      dict.Add("Instant", (object) task.IsInstant);
      if (task.Disabled)
        dict.Add("Disabled", (object) task.Disabled);
      JSONSerialization.SerializeFields((object) task, ref dict, ref unityObjects);
      if (serializeChildren && task is ParentTask)
      {
        ParentTask parentTask = task as ParentTask;
        if (parentTask.Children != null && parentTask.Children.Count > 0)
        {
          Dictionary<string, object>[] dictionaryArray = new Dictionary<string, object>[parentTask.Children.Count];
          for (int index = 0; index < parentTask.Children.Count; ++index)
            dictionaryArray[index] = JSONSerialization.SerializeTask(parentTask.Children[index], serializeChildren, ref unityObjects);
          dict.Add("Children", (object) dictionaryArray);
        }
      }
      return dict;
    }

    private static Dictionary<string, object> SerializeNodeData(NodeData nodeData)
    {
      Dictionary<string, object> dictionary = new Dictionary<string, object>();
      dictionary.Add("Offset", (object) nodeData.Offset);
      if (nodeData.Comment.Length > 0)
        dictionary.Add("Comment", (object) nodeData.Comment);
      if (nodeData.IsBreakpoint)
        dictionary.Add("IsBreakpoint", (object) nodeData.IsBreakpoint);
      if (nodeData.Collapsed)
        dictionary.Add("Collapsed", (object) nodeData.Collapsed);
      if (nodeData.ColorIndex != 0)
        dictionary.Add("ColorIndex", (object) nodeData.ColorIndex);
      if (nodeData.WatchedFieldNames != null && nodeData.WatchedFieldNames.Count > 0)
        dictionary.Add("WatchedFields", (object) nodeData.WatchedFieldNames);
      return dictionary;
    }

    private static Dictionary<string, object> SerializeVariable(
      SharedVariable sharedVariable,
      ref List<UnityEngine.Object> unityObjects)
    {
      if (sharedVariable == null)
        return (Dictionary<string, object>) null;
      Dictionary<string, object> dict = new Dictionary<string, object>();
      dict.Add("Type", (object) ((object) sharedVariable).GetType());
      dict.Add("Name", (object) sharedVariable.Name);
      if (sharedVariable.IsShared)
        dict.Add("IsShared", (object) sharedVariable.IsShared);
      if (sharedVariable.IsGlobal)
        dict.Add("IsGlobal", (object) sharedVariable.IsGlobal);
      if (sharedVariable.IsDynamic)
        dict.Add("IsDynamic", (object) sharedVariable.IsDynamic);
      if (!string.IsNullOrEmpty(sharedVariable.Tooltip))
        dict.Add("Tooltip", (object) sharedVariable.Tooltip);
      if (!string.IsNullOrEmpty(sharedVariable.PropertyMapping))
      {
        dict.Add("PropertyMapping", (object) sharedVariable.PropertyMapping);
        if (!object.Equals((object) sharedVariable.PropertyMappingOwner, (object) null))
        {
          dict.Add("PropertyMappingOwner", (object) unityObjects.Count);
          unityObjects.Add((UnityEngine.Object) sharedVariable.PropertyMappingOwner);
        }
      }
      JSONSerialization.SerializeFields((object) sharedVariable, ref dict, ref unityObjects);
      return dict;
    }

    private static void SerializeFields(
      object obj,
      ref Dictionary<string, object> dict,
      ref List<UnityEngine.Object> unityObjects)
    {
      FieldInfo[] serializableFields = TaskUtility.GetSerializableFields(obj.GetType());
      for (int index1 = 0; index1 < serializableFields.Length; ++index1)
      {
        if (!BehaviorDesignerUtility.HasAttribute(serializableFields[index1], typeof (NonSerializedAttribute)) && (!serializableFields[index1].IsPrivate && !serializableFields[index1].IsFamily || BehaviorDesignerUtility.HasAttribute(serializableFields[index1], typeof (SerializeField))) && (!(obj is ParentTask) || !serializableFields[index1].Name.Equals("children")) && serializableFields[index1].GetValue(obj) != null)
        {
          string key = (serializableFields[index1].FieldType.Name + serializableFields[index1].Name).ToString();
          if (typeof (IList).IsAssignableFrom(serializableFields[index1].FieldType))
          {
            if (serializableFields[index1].GetValue(obj) is IList list)
            {
              List<object> objectList = new List<object>();
              for (int index2 = 0; index2 < list.Count; ++index2)
              {
                if (list[index2] == null)
                {
                  objectList.Add((object) null);
                }
                else
                {
                  System.Type type = list[index2].GetType();
                  if (list[index2] is Task && !TaskUtility.HasAttribute(serializableFields[index1], typeof (InspectTaskAttribute)))
                  {
                    Task task = list[index2] as Task;
                    objectList.Add((object) task.ID);
                  }
                  else if (list[index2] is SharedVariable)
                    objectList.Add((object) JSONSerialization.SerializeVariable(list[index2] as SharedVariable, ref unityObjects));
                  else if ((object) (list[index2] as UnityEngine.Object) != null)
                  {
                    UnityEngine.Object objA = list[index2] as UnityEngine.Object;
                    if (!object.ReferenceEquals((object) objA, (object) null) && objA != (UnityEngine.Object) null)
                    {
                      objectList.Add((object) unityObjects.Count);
                      unityObjects.Add(objA);
                    }
                  }
                  else if (type.Equals(typeof (LayerMask)))
                    objectList.Add((object) ((LayerMask) list[index2]).value);
                  else if (type.IsPrimitive || type.IsEnum || type.Equals(typeof (string)) || type.Equals(typeof (Vector2)) || type.Equals(typeof (Vector2Int)) || type.Equals(typeof (Vector3)) || type.Equals(typeof (Vector3Int)) || type.Equals(typeof (Vector4)) || type.Equals(typeof (Quaternion)) || type.Equals(typeof (Matrix4x4)) || type.Equals(typeof (Color)) || type.Equals(typeof (Rect)))
                  {
                    objectList.Add(list[index2]);
                  }
                  else
                  {
                    Dictionary<string, object> dict1 = new Dictionary<string, object>();
                    JSONSerialization.SerializeFields(list[index2], ref dict1, ref unityObjects);
                    objectList.Add((object) new Dictionary<string, object>()
                    {
                      {
                        "Type",
                        (object) list[index2].GetType().FullName
                      },
                      {
                        "Value",
                        (object) dict1
                      }
                    });
                  }
                }
              }
              if (objectList != null)
                dict.Add(key, (object) objectList);
            }
          }
          else if (typeof (Task).IsAssignableFrom(serializableFields[index1].FieldType))
          {
            if (serializableFields[index1].GetValue(obj) is Task task1)
            {
              if (BehaviorDesignerUtility.HasAttribute(serializableFields[index1], typeof (InspectTaskAttribute)))
              {
                Dictionary<string, object> dict2 = new Dictionary<string, object>();
                dict2.Add("Type", (object) ((object) task1).GetType());
                JSONSerialization.SerializeFields((object) task1, ref dict2, ref unityObjects);
                dict.Add(key, (object) dict2);
              }
              else
                dict.Add(key, (object) task1.ID);
            }
          }
          else if (typeof (SharedVariable).IsAssignableFrom(serializableFields[index1].FieldType))
          {
            if (!dict.ContainsKey(key))
              dict.Add(key, (object) JSONSerialization.SerializeVariable(serializableFields[index1].GetValue(obj) as SharedVariable, ref unityObjects));
          }
          else if (typeof (UnityEngine.Object).IsAssignableFrom(serializableFields[index1].FieldType))
          {
            UnityEngine.Object objA = serializableFields[index1].GetValue(obj) as UnityEngine.Object;
            if (!object.ReferenceEquals((object) objA, (object) null) && objA != (UnityEngine.Object) null)
            {
              dict.Add(key, (object) unityObjects.Count);
              unityObjects.Add(objA);
            }
          }
          else if (serializableFields[index1].FieldType.Equals(typeof (LayerMask)))
            dict.Add(key, (object) ((LayerMask) serializableFields[index1].GetValue(obj)).value);
          else if (serializableFields[index1].FieldType.IsPrimitive || serializableFields[index1].FieldType.IsEnum || serializableFields[index1].FieldType.Equals(typeof (string)) || serializableFields[index1].FieldType.Equals(typeof (Vector2)) || serializableFields[index1].FieldType.Equals(typeof (Vector2Int)) || serializableFields[index1].FieldType.Equals(typeof (Vector3)) || serializableFields[index1].FieldType.Equals(typeof (Vector3Int)) || serializableFields[index1].FieldType.Equals(typeof (Vector4)) || serializableFields[index1].FieldType.Equals(typeof (Quaternion)) || serializableFields[index1].FieldType.Equals(typeof (Matrix4x4)) || serializableFields[index1].FieldType.Equals(typeof (Color)) || serializableFields[index1].FieldType.Equals(typeof (Rect)))
            dict.Add(key, serializableFields[index1].GetValue(obj));
          else if (serializableFields[index1].FieldType.Equals(typeof (AnimationCurve)))
          {
            AnimationCurve animationCurve = serializableFields[index1].GetValue(obj) as AnimationCurve;
            Dictionary<string, object> dictionary = new Dictionary<string, object>();
            if (animationCurve.keys != null)
            {
              Keyframe[] keys = animationCurve.keys;
              List<List<object>> objectListList = new List<List<object>>();
              for (int index3 = 0; index3 < keys.Length; ++index3)
                objectListList.Add(new List<object>()
                {
                  (object) keys[index3].time,
                  (object) keys[index3].value,
                  (object) keys[index3].inTangent,
                  (object) keys[index3].outTangent
                });
              dictionary.Add("Keys", (object) objectListList);
            }
            dictionary.Add("PreWrapMode", (object) animationCurve.preWrapMode);
            dictionary.Add("PostWrapMode", (object) animationCurve.postWrapMode);
            dict.Add(key, (object) dictionary);
          }
          else
          {
            Dictionary<string, object> dict3 = new Dictionary<string, object>();
            JSONSerialization.SerializeFields(serializableFields[index1].GetValue(obj), ref dict3, ref unityObjects);
            dict.Add(key, (object) dict3);
          }
        }
      }
    }
  }
}
