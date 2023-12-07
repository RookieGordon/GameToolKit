// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.ErrorCheck
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
    public static class ErrorCheck
    {
        private static HashSet<int> fieldHashes = new HashSet<int>();

        public static List<ErrorDetails> CheckForErrors(BehaviorSource behaviorSource)
        {
            if (behaviorSource == null || behaviorSource.Owner == null)
                return (List<ErrorDetails>)null;
            List<ErrorDetails> errorDetails = (List<ErrorDetails>)null;
            ErrorCheck.fieldHashes.Clear();
            BehaviorSource behaviorSource1 = behaviorSource;
            if (!Application.isPlaying && behaviorSource.Owner is Behavior && (UnityEngine.Object)(behaviorSource.Owner as Behavior).ExternalBehavior != (UnityEngine.Object)null)
                behaviorSource = (behaviorSource.Owner as Behavior).ExternalBehavior.BehaviorSource;
            bool projectLevelBehavior = AssetDatabase.GetAssetPath(behaviorSource.Owner.GetObject()).Length > 0;
            if (behaviorSource.EntryTask != null)
            {
                ErrorCheck.CheckTaskForErrors(behaviorSource.EntryTask, projectLevelBehavior, ref errorDetails);
                if (behaviorSource.RootTask == null)
                    ErrorCheck.AddError(ref errorDetails, ErrorDetails.ErrorType.MissingChildren, behaviorSource.EntryTask, (string)null);
            }

            if (behaviorSource.RootTask != null)
                ErrorCheck.CheckTaskForErrors(behaviorSource.RootTask, projectLevelBehavior, ref errorDetails);
            if (!EditorApplication.isPlaying && AssetDatabase.GetAssetPath(behaviorSource1.Owner.GetObject()).Length > 0 && behaviorSource1.Variables != null)
            {
                for (int index = 0; index < behaviorSource1.Variables.Count; ++index)
                {
                    if (behaviorSource1.Variables[index] != null)
                    {
                        object obj = behaviorSource1.Variables[index].GetValue();
                        if (obj != null && (object)(obj as UnityEngine.Object) != null && AssetDatabase.GetAssetPath(obj as UnityEngine.Object).Length == 0)
                            ErrorCheck.AddError(ref errorDetails, ErrorDetails.ErrorType.InvalidVariableReference, (Task)null, behaviorSource1.Variables[index].Name);
                    }
                }
            }

            return errorDetails;
        }

        private static void CheckTaskForErrors(
            Task task,
            bool projectLevelBehavior,
            ref List<ErrorDetails> errorDetails)
        {
            if (task.Disabled)
                return;
            if (task is UnknownTask || task is UnknownParentTask)
                ErrorCheck.AddError(ref errorDetails, ErrorDetails.ErrorType.UnknownTask, task, (string)null);
            if (((object)task).GetType().GetCustomAttributes(typeof(SkipErrorCheckAttribute), false).Length == 0)
            {
                FieldInfo[] serializableFields = TaskUtility.GetSerializableFields(((object)task).GetType());
                for (int index = 0; index < serializableFields.Length; ++index)
                    ErrorCheck.CheckField(task, projectLevelBehavior, ref errorDetails, serializableFields[index], 0, serializableFields[index].GetValue((object)task));
            }

            if (!(task is ParentTask) || task.NodeData.NodeDesigner == null || (task.NodeData.NodeDesigner as NodeDesigner).IsEntryDisplay)
                return;
            ParentTask parentTask = task as ParentTask;
            if (parentTask.Children == null || parentTask.Children.Count == 0)
            {
                ErrorCheck.AddError(ref errorDetails, ErrorDetails.ErrorType.MissingChildren, task, (string)null);
            }
            else
            {
                for (int index = 0; index < parentTask.Children.Count; ++index)
                    ErrorCheck.CheckTaskForErrors(parentTask.Children[index], projectLevelBehavior, ref errorDetails);
            }
        }

        private static void CheckField(
            Task task,
            bool projectLevelBehavior,
            ref List<ErrorDetails> errorDetails,
            FieldInfo field,
            int hashPrefix,
            object value)
        {
            if (value == null)
                return;
            int hashPrefix1 = hashPrefix + field.Name.GetHashCode() + field.GetHashCode();
            if (ErrorCheck.fieldHashes.Contains(hashPrefix1))
                return;
            ErrorCheck.fieldHashes.Add(hashPrefix1);
            if (TaskUtility.HasAttribute(field, typeof(RequiredFieldAttribute)) && !ErrorCheck.IsRequiredFieldValid(field.FieldType, value))
                ErrorCheck.AddError(ref errorDetails, ErrorDetails.ErrorType.RequiredField, task, field.Name);
            if (typeof(SharedVariable).IsAssignableFrom(field.FieldType))
            {
                if (!(value is SharedVariable sharedVariable))
                    return;
                if (sharedVariable.IsShared && !sharedVariable.IsDynamic && string.IsNullOrEmpty(sharedVariable.Name) && !TaskUtility.HasAttribute(field, typeof(SharedRequiredAttribute)))
                    ErrorCheck.AddError(ref errorDetails, ErrorDetails.ErrorType.SharedVariable, task, field.Name);
                SharedVariable variable;
                if (!Application.isPlaying && sharedVariable.IsShared && sharedVariable.IsDynamic && !string.IsNullOrEmpty(sharedVariable.Name) && (UnityEngine.Object)task.Owner != (UnityEngine.Object)null && (variable = task.Owner.GetBehaviorSource().GetVariable(sharedVariable.Name)) != null &&
                    !variable.IsDynamic)
                    ErrorCheck.AddError(ref errorDetails, ErrorDetails.ErrorType.NonUniqueDynamicVariable, task, field.Name);
                object obj = sharedVariable.GetValue();
                if (EditorApplication.isPlaying || !projectLevelBehavior || sharedVariable.IsShared || (object)(obj as UnityEngine.Object) == null || AssetDatabase.GetAssetPath(obj as UnityEngine.Object).Length > 0)
                    return;
                ErrorCheck.AddError(ref errorDetails, ErrorDetails.ErrorType.InvalidTaskReference, task, field.Name);
            }
            else if ((object)(value as UnityEngine.Object) != null)
            {
                bool flag = AssetDatabase.GetAssetPath(value as UnityEngine.Object).Length > 0;
                if (EditorApplication.isPlaying || !projectLevelBehavior || flag)
                    return;
                ErrorCheck.AddError(ref errorDetails, ErrorDetails.ErrorType.InvalidTaskReference, task, field.Name);
            }
            else
            {
                if (typeof(Delegate).IsAssignableFrom(field.FieldType) || typeof(Task).IsAssignableFrom(field.FieldType) || typeof(Behavior).IsAssignableFrom(field.FieldType) || !field.FieldType.IsClass && (!field.FieldType.IsValueType || field.FieldType.IsPrimitive))
                    return;
                FieldInfo[] serializableFields = TaskUtility.GetSerializableFields(field.FieldType);
                for (int index = 0; index < serializableFields.Length; ++index)
                    ErrorCheck.CheckField(task, projectLevelBehavior, ref errorDetails, serializableFields[index], hashPrefix1, serializableFields[index].GetValue(value));
            }
        }

        private static void AddError(
            ref List<ErrorDetails> errorDetails,
            ErrorDetails.ErrorType type,
            Task task,
            string fieldName)
        {
            if (errorDetails == null)
                errorDetails = new List<ErrorDetails>();
            errorDetails.Add(new ErrorDetails(type, task, fieldName));
        }

        public static bool IsRequiredFieldValid(System.Type fieldType, object value)
        {
            if (value == null || value.Equals((object)null))
                return false;
            if (typeof(IList).IsAssignableFrom(fieldType))
            {
                IList list = value as IList;
                if (list.Count == 0)
                    return false;
                for (int index = 0; index < list.Count; ++index)
                {
                    if (list[index] == null || list[index].Equals((object)null))
                        return false;
                }
            }

            return true;
        }
    }
}