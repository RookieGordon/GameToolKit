// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.TaskReferences
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
  public class TaskReferences : MonoBehaviour
  {
    public static void CheckReferences(BehaviorSource behaviorSource)
    {
      if (behaviorSource.RootTask != null)
        TaskReferences.CheckReferences(behaviorSource, behaviorSource.RootTask);
      if (behaviorSource.DetachedTasks == null)
        return;
      for (int index = 0; index < behaviorSource.DetachedTasks.Count; ++index)
        TaskReferences.CheckReferences(behaviorSource, behaviorSource.DetachedTasks[index]);
    }

    private static void CheckReferences(BehaviorSource behaviorSource, Task task)
    {
      FieldInfo[] serializableFields = TaskUtility.GetSerializableFields(((object) task).GetType());
      for (int index1 = 0; index1 < serializableFields.Length; ++index1)
      {
        if (!serializableFields[index1].FieldType.IsArray && (serializableFields[index1].FieldType.Equals(typeof (Task)) || serializableFields[index1].FieldType.IsSubclassOf(typeof (Task))))
        {
          if (serializableFields[index1].GetValue((object) task) is Task referencedTask1)
          {
            Task referencedTask = TaskReferences.FindReferencedTask(behaviorSource, referencedTask1);
            if (referencedTask != null)
              serializableFields[index1].SetValue((object) task, (object) referencedTask);
          }
        }
        else if (serializableFields[index1].FieldType.IsArray && (serializableFields[index1].FieldType.GetElementType().Equals(typeof (Task)) || serializableFields[index1].FieldType.GetElementType().IsSubclassOf(typeof (Task))) && serializableFields[index1].GetValue((object) task) is Task[] taskArray)
        {
          IList instance1 = Activator.CreateInstance(typeof (List<>).MakeGenericType(serializableFields[index1].FieldType.GetElementType())) as IList;
          if (!BehaviorDesignerUtility.HasAttribute(serializableFields[index1], typeof (InspectTaskAttribute)))
          {
            for (int index2 = 0; index2 < taskArray.Length; ++index2)
            {
              Task referencedTask2 = TaskReferences.FindReferencedTask(behaviorSource, taskArray[index2]);
              if (referencedTask2 != null)
                instance1.Add((object) referencedTask2);
            }
            Array instance2 = Array.CreateInstance(serializableFields[index1].FieldType.GetElementType(), instance1.Count);
            instance1.CopyTo(instance2, 0);
            serializableFields[index1].SetValue((object) task, (object) instance2);
          }
        }
      }
      if (!((object) task).GetType().IsSubclassOf(typeof (ParentTask)))
        return;
      ParentTask parentTask = task as ParentTask;
      if (parentTask.Children == null)
        return;
      for (int index = 0; index < parentTask.Children.Count; ++index)
        TaskReferences.CheckReferences(behaviorSource, parentTask.Children[index]);
    }

    private static Task FindReferencedTask(BehaviorSource behaviorSource, Task referencedTask)
    {
      if (referencedTask == null)
        return (Task) null;
      int id = referencedTask.ID;
      Task referencedTask1;
      if (behaviorSource.RootTask != null && (referencedTask1 = TaskReferences.FindReferencedTask(behaviorSource.RootTask, id)) != null)
        return referencedTask1;
      if (behaviorSource.DetachedTasks != null)
      {
        for (int index = 0; index < behaviorSource.DetachedTasks.Count; ++index)
        {
          Task referencedTask2;
          if ((referencedTask2 = TaskReferences.FindReferencedTask(behaviorSource.DetachedTasks[index], id)) != null)
            return referencedTask2;
        }
      }
      return (Task) null;
    }

    private static Task FindReferencedTask(Task task, int referencedTaskID)
    {
      if (task.ID == referencedTaskID)
        return task;
      if (((object) task).GetType().IsSubclassOf(typeof (ParentTask)))
      {
        ParentTask parentTask = task as ParentTask;
        if (parentTask.Children != null)
        {
          for (int index = 0; index < parentTask.Children.Count; ++index)
          {
            Task referencedTask;
            if ((referencedTask = TaskReferences.FindReferencedTask(parentTask.Children[index], referencedTaskID)) != null)
              return referencedTask;
          }
        }
      }
      return (Task) null;
    }

    public static void CheckReferences(Behavior behavior, List<Task> taskList)
    {
      for (int index = 0; index < taskList.Count; ++index)
        TaskReferences.CheckReferences(behavior, taskList[index], taskList);
    }

    private static void CheckReferences(Behavior behavior, Task task, List<Task> taskList)
    {
      if (TaskUtility.CompareType(((object) task).GetType(), "BehaviorDesigner.Runtime.Tasks.ConditionalEvaluator"))
      {
        object obj = ((object) task).GetType().GetField("conditionalTask").GetValue((object) task);
        if (obj != null)
          task = obj as Task;
      }
      FieldInfo[] serializableFields = TaskUtility.GetSerializableFields(((object) task).GetType());
      for (int index1 = 0; index1 < serializableFields.Length; ++index1)
      {
        if (!serializableFields[index1].FieldType.IsArray && (serializableFields[index1].FieldType.Equals(typeof (Task)) || serializableFields[index1].FieldType.IsSubclassOf(typeof (Task))))
        {
          if (serializableFields[index1].GetValue((object) task) is Task referencedTask1 && !((object) referencedTask1.Owner).Equals((object) behavior))
          {
            Task referencedTask = TaskReferences.FindReferencedTask(referencedTask1, taskList);
            if (referencedTask != null)
              serializableFields[index1].SetValue((object) task, (object) referencedTask);
          }
        }
        else if (serializableFields[index1].FieldType.IsArray && (serializableFields[index1].FieldType.GetElementType().Equals(typeof (Task)) || serializableFields[index1].FieldType.GetElementType().IsSubclassOf(typeof (Task))) && serializableFields[index1].GetValue((object) task) is Task[] taskArray)
        {
          IList instance1 = Activator.CreateInstance(typeof (List<>).MakeGenericType(serializableFields[index1].FieldType.GetElementType())) as IList;
          for (int index2 = 0; index2 < taskArray.Length; ++index2)
          {
            Task referencedTask2 = TaskReferences.FindReferencedTask(taskArray[index2], taskList);
            if (referencedTask2 != null)
              instance1.Add((object) referencedTask2);
          }
          Array instance2 = Array.CreateInstance(serializableFields[index1].FieldType.GetElementType(), instance1.Count);
          instance1.CopyTo(instance2, 0);
          serializableFields[index1].SetValue((object) task, (object) instance2);
        }
      }
    }

    private static Task FindReferencedTask(Task referencedTask, List<Task> taskList)
    {
      int referenceId = referencedTask.ReferenceID;
      for (int index = 0; index < taskList.Count; ++index)
      {
        if (taskList[index].ReferenceID == referenceId)
          return taskList[index];
      }
      return (Task) null;
    }
  }
}
