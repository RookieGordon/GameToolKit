// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.TaskInspector
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
using TooltipAttribute = BehaviorDesigner.Runtime.Tasks.TooltipAttribute;
using HelpURLAttribute = BehaviorDesigner.Runtime.Tasks.HelpURLAttribute;

namespace BehaviorDesigner.Editor
{
  [Serializable]
  public class TaskInspector : ScriptableObject
  {
    private BehaviorDesignerWindow behaviorDesignerWindow;
    private Task activeReferenceTask;
    private FieldInfo activeReferenceTaskFieldInfo;
    private Task mActiveMenuSelectionTask;
    private Vector2 mScrollPosition = Vector2.zero;

    public Task ActiveReferenceTask => this.activeReferenceTask;

    public FieldInfo ActiveReferenceTaskFieldInfo => this.activeReferenceTaskFieldInfo;

    public void OnEnable() => this.hideFlags = HideFlags.HideAndDontSave;

    public void ClearFocus() => GUIUtility.keyboardControl = 0;

    public bool HasFocus() => GUIUtility.keyboardControl != 0;

    public bool DrawTaskInspector(
      BehaviorSource behaviorSource,
      TaskList taskList,
      Task task,
      bool enabled)
    {
      if (task == null || (task.NodeData.NodeDesigner as NodeDesigner).IsEntryDisplay)
        return false;
      this.mScrollPosition = GUILayout.BeginScrollView(this.mScrollPosition, Array.Empty<GUILayoutOption>());
      GUI.enabled = enabled;
      if ((UnityEngine.Object) this.behaviorDesignerWindow == (UnityEngine.Object) null)
        this.behaviorDesignerWindow = BehaviorDesignerWindow.instance;
      GUILayout.Space(6f);
      EditorGUIUtility.labelWidth = 150f;
      EditorGUI.BeginChangeCheck();
      GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
      EditorGUILayout.LabelField("Name", new GUILayoutOption[1]
      {
        GUILayout.Width(90f)
      });
      task.FriendlyName = EditorGUILayout.TextField(task.FriendlyName, Array.Empty<GUILayoutOption>());
      if (GUILayout.Button((Texture) BehaviorDesignerUtility.DocTexture, BehaviorDesignerUtility.TransparentButtonOffsetGUIStyle, Array.Empty<GUILayoutOption>()))
        this.OpenHelpURL(task);
      if (GUILayout.Button((Texture) BehaviorDesignerUtility.ColorSelectorTexture(task.NodeData.ColorIndex), BehaviorDesignerUtility.TransparentButtonOffsetGUIStyle, Array.Empty<GUILayoutOption>()))
      {
        GenericMenu menu = new GenericMenu();
        this.AddColorMenuItem(ref menu, task, "Default", 0);
        this.AddColorMenuItem(ref menu, task, "Red", 1);
        this.AddColorMenuItem(ref menu, task, "Pink", 2);
        this.AddColorMenuItem(ref menu, task, "Brown", 3);
        this.AddColorMenuItem(ref menu, task, "Orange", 4);
        this.AddColorMenuItem(ref menu, task, "Turquoise", 5);
        this.AddColorMenuItem(ref menu, task, "Cyan", 6);
        this.AddColorMenuItem(ref menu, task, "Blue", 7);
        this.AddColorMenuItem(ref menu, task, "Purple", 8);
        menu.ShowAsContext();
      }
      if (GUILayout.Button((Texture) BehaviorDesignerUtility.GearTexture, BehaviorDesignerUtility.TransparentButtonOffsetGUIStyle, Array.Empty<GUILayoutOption>()))
      {
        GenericMenu genericMenu1 = new GenericMenu();
        GenericMenu genericMenu2 = genericMenu1;
        GUIContent guiContent1 = new GUIContent("Edit Script");
        Task task1 = task;
        genericMenu2.AddItem(guiContent1, false, OpenInFileEditor, (object) task1);
        GenericMenu genericMenu3 = genericMenu1;
        GUIContent guiContent2 = new GUIContent("Locate Script");
        Task task2 = task;
        genericMenu3.AddItem(guiContent2, false, SelectInProject, (object) task2);
        // ISSUE: method pointer
        genericMenu1.AddItem(new GUIContent("Reset"), false, ResetTask, (object) task);
        genericMenu1.ShowAsContext();
      }
      GUILayout.EndHorizontal();
      string str = BehaviorDesignerUtility.SplitCamelCase(((object) task).GetType().Name.ToString());
      if (!task.FriendlyName.Equals(str))
      {
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        EditorGUILayout.LabelField("Type", new GUILayoutOption[1]
        {
          GUILayout.Width(90f)
        });
        EditorGUILayout.LabelField(str, new GUILayoutOption[1]
        {
          GUILayout.MaxWidth(170f)
        });
        GUILayout.EndHorizontal();
      }
      GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
      EditorGUILayout.LabelField("Instant", new GUILayoutOption[1]
      {
        GUILayout.Width(90f)
      });
      task.IsInstant = EditorGUILayout.Toggle(task.IsInstant, Array.Empty<GUILayoutOption>());
      GUILayout.EndHorizontal();
      EditorGUILayout.LabelField("Comment", Array.Empty<GUILayoutOption>());
      task.NodeData.Comment = EditorGUILayout.TextArea(task.NodeData.Comment, BehaviorDesignerUtility.TaskInspectorCommentGUIStyle, new GUILayoutOption[1]
      {
        GUILayout.Height(48f)
      });
      if (EditorGUI.EndChangeCheck())
      {
        BehaviorUndo.RegisterUndo("Inspector", behaviorSource.Owner.GetObject());
        GUI.changed = true;
      }
      BehaviorDesignerUtility.DrawContentSeperator(2);
      GUILayout.Space(6f);
      if (this.DrawTaskFields(behaviorSource, taskList, task, enabled))
      {
        BehaviorUndo.RegisterUndo("Inspector", behaviorSource.Owner.GetObject());
        GUI.changed = true;
      }
      GUI.enabled = true;
      GUILayout.EndScrollView();
      return GUI.changed;
    }

    private bool DrawTaskFields(
      BehaviorSource behaviorSource,
      TaskList taskList,
      Task task,
      bool enabled)
    {
      if (task == null)
        return false;
      EditorGUI.BeginChangeCheck();
      FieldInspector.behaviorSource = behaviorSource;
      this.DrawObjectFields(behaviorSource, taskList, task, (object) task, enabled, true);
      return EditorGUI.EndChangeCheck();
    }

    private void DrawObjectFields(
      BehaviorSource behaviorSource,
      TaskList taskList,
      Task task,
      object obj,
      bool enabled,
      bool drawWatch)
    {
      if (obj == null || (UnityEngine.Object) BehaviorDesignerWindow.instance == (UnityEngine.Object) null)
        return;
      ObjectDrawer objectDrawer;
      if ((objectDrawer = ObjectDrawerUtility.GetObjectDrawer(task)) != null)
      {
        objectDrawer.OnGUI(new GUIContent());
      }
      else
      {
        List<System.Type> baseClasses = FieldInspector.GetBaseClasses(obj.GetType());
        BindingFlags bindingAttr = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        bool isReflectionTask = this.IsReflectionTask(obj.GetType());
        for (int index1 = baseClasses.Count - 1; index1 > -1; --index1)
        {
          FieldInfo[] fields = baseClasses[index1].GetFields(bindingAttr);
          for (int index2 = 0; index2 < fields.Length; ++index2)
          {
            if (!BehaviorDesignerUtility.HasAttribute(fields[index2], typeof (NonSerializedAttribute)) && !BehaviorDesignerUtility.HasAttribute(fields[index2], typeof (HideInInspector)) && (!fields[index2].IsPrivate && !fields[index2].IsFamily || BehaviorDesignerUtility.HasAttribute(fields[index2], typeof (SerializeField))) && (!(obj is ParentTask) || !fields[index2].Name.Equals("children")) && (!isReflectionTask || !fields[index2].FieldType.Equals(typeof (SharedVariable)) && !fields[index2].FieldType.IsSubclassOf(typeof (SharedVariable)) || this.CanDrawReflectedField(obj, fields[index2])))
            {
              HeaderAttribute[] customAttributes1;
              if ((customAttributes1 = fields[index2].GetCustomAttributes(typeof (HeaderAttribute), true) as HeaderAttribute[]).Length > 0)
                EditorGUILayout.LabelField(customAttributes1[0].header, BehaviorDesignerUtility.BoldLabelGUIStyle, Array.Empty<GUILayoutOption>());
              SpaceAttribute[] customAttributes2;
              if ((customAttributes2 = fields[index2].GetCustomAttributes(typeof (SpaceAttribute), true) as SpaceAttribute[]).Length > 0)
                GUILayout.Space(customAttributes2[0].height);
              string s = fields[index2].Name;
              if (isReflectionTask && (fields[index2].FieldType.Equals(typeof (SharedVariable)) || fields[index2].FieldType.IsSubclassOf(typeof (SharedVariable))))
                s = this.InvokeParameterName(obj, fields[index2]);
              TooltipAttribute[] customAttributes3;
              GUIContent guiContent = (customAttributes3 = fields[index2].GetCustomAttributes(typeof (TooltipAttribute), false) as TooltipAttribute[]).Length <= 0 ? new GUIContent(BehaviorDesignerUtility.SplitCamelCase(s)) : new GUIContent(BehaviorDesignerUtility.SplitCamelCase(s), customAttributes3[0].Tooltip);
              object obj1 = fields[index2].GetValue(obj);
              System.Type fieldType = fields[index2].FieldType;
              if (typeof (Task).IsAssignableFrom(fieldType) || typeof (IList).IsAssignableFrom(fieldType) && (typeof (Task).IsAssignableFrom(fieldType.GetElementType()) || fieldType.IsGenericType && typeof (Task).IsAssignableFrom(fieldType.GetGenericArguments()[0])))
              {
                EditorGUI.BeginChangeCheck();
                this.DrawTaskValue(behaviorSource, taskList, fields[index2], guiContent, task, obj1 as Task, enabled);
                if (BehaviorDesignerWindow.instance.ContainsError(task, fields[index2].Name))
                {
                  GUILayout.Space(-3f);
                  GUILayout.Box((Texture) BehaviorDesignerUtility.ErrorIconTexture, BehaviorDesignerUtility.PlainTextureGUIStyle, new GUILayoutOption[1]
                  {
                    GUILayout.Width(20f)
                  });
                }
                if (EditorGUI.EndChangeCheck())
                  GUI.changed = true;
              }
              else if (fieldType.Equals(typeof (SharedVariable)) || fieldType.IsSubclassOf(typeof (SharedVariable)))
              {
                GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
                EditorGUI.BeginChangeCheck();
                if (drawWatch)
                  this.DrawWatchedButton(task, fields[index2]);
                SharedVariable sharedVariable = this.DrawSharedVariableValue(behaviorSource, fields[index2], guiContent, task, obj1 as SharedVariable, isReflectionTask, enabled, drawWatch);
                if (BehaviorDesignerWindow.instance.ContainsError(task, fields[index2].Name))
                {
                  GUILayout.Space(-3f);
                  GUILayout.Box((Texture) BehaviorDesignerUtility.ErrorIconTexture, BehaviorDesignerUtility.PlainTextureGUIStyle, new GUILayoutOption[1]
                  {
                    GUILayout.Width(20f)
                  });
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(4f);
                if (EditorGUI.EndChangeCheck())
                {
                  fields[index2].SetValue(obj, (object) sharedVariable);
                  GUI.changed = true;
                }
              }
              else
              {
                GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
                EditorGUI.BeginChangeCheck();
                if (drawWatch)
                  this.DrawWatchedButton(task, fields[index2]);
                object obj2 = FieldInspector.DrawField(task, guiContent, fields[index2], obj1);
                if (BehaviorDesignerWindow.instance.ContainsError(task, fields[index2].Name))
                {
                  GUILayout.Space(-3f);
                  GUILayout.Box((Texture) BehaviorDesignerUtility.ErrorIconTexture, BehaviorDesignerUtility.PlainTextureGUIStyle, new GUILayoutOption[1]
                  {
                    GUILayout.Width(20f)
                  });
                }
                if (EditorGUI.EndChangeCheck())
                {
                  fields[index2].SetValue(obj, obj2);
                  GUI.changed = true;
                }
                if (TaskUtility.HasAttribute(fields[index2], typeof (RequiredFieldAttribute)) && !ErrorCheck.IsRequiredFieldValid(fieldType, obj1))
                {
                  GUILayout.Space(-3f);
                  GUILayout.Box((Texture) BehaviorDesignerUtility.ErrorIconTexture, BehaviorDesignerUtility.PlainTextureGUIStyle, new GUILayoutOption[1]
                  {
                    GUILayout.Width(20f)
                  });
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(4f);
              }
            }
          }
        }
      }
    }

    private bool DrawWatchedButton(Task task, FieldInfo field)
    {
      GUILayout.Space(3f);
      bool flag = task.NodeData.GetWatchedFieldIndex(field) != -1;
      if (!GUILayout.Button((Texture) (!flag ? BehaviorDesignerUtility.VariableWatchButtonTexture : BehaviorDesignerUtility.VariableWatchButtonSelectedTexture), BehaviorDesignerUtility.PlainButtonGUIStyle, new GUILayoutOption[1]
      {
        GUILayout.Width(15f)
      }))
        return false;
      if (flag)
        task.NodeData.RemoveWatchedField(field);
      else
        task.NodeData.AddWatchedField(field);
      return true;
    }

    private void DrawTaskValue(
      BehaviorSource behaviorSource,
      TaskList taskList,
      FieldInfo field,
      GUIContent guiContent,
      Task parentTask,
      Task task,
      bool enabled)
    {
      if (BehaviorDesignerUtility.HasAttribute(field, typeof (InspectTaskAttribute)))
      {
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUILayout.Label(guiContent, new GUILayoutOption[1]
        {
          GUILayout.Width(144f)
        });
        if (GUILayout.Button(task == null ? "Select" : BehaviorDesignerUtility.SplitCamelCase(((object) task).GetType().Name.ToString()), EditorStyles.toolbarPopup, new GUILayoutOption[1]
        {
          GUILayout.Width(134f)
        }))
        {
          GenericMenu genericMenu = new GenericMenu();
          // ISSUE: method pointer
          genericMenu.AddItem(new GUIContent("None"), task == null, InspectedTaskCallback, (object) null);
          // ISSUE: method pointer
          taskList.AddTaskTypesToMenu(2, ref genericMenu, task == null ? (System.Type) null : ((object) task).GetType(), (System.Type) null, string.Empty, true, InspectedTaskCallback);
          genericMenu.ShowAsContext();
          this.mActiveMenuSelectionTask = parentTask;
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(2f);
        this.DrawObjectFields(behaviorSource, taskList, task, (object) task, enabled, false);
      }
      else
      {
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        this.DrawWatchedButton(parentTask, field);
        GUILayout.Label(guiContent, BehaviorDesignerUtility.TaskInspectorGUIStyle, new GUILayoutOption[1]
        {
          GUILayout.Width(165f)
        });
        bool flag = this.behaviorDesignerWindow.IsReferencingField(field);
        Color backgroundColor = GUI.backgroundColor;
        if (flag)
          GUI.backgroundColor = new Color(0.5f, 1f, 0.5f);
        if (GUILayout.Button(!flag ? "Select" : "Done", EditorStyles.miniButtonMid, new GUILayoutOption[1]
        {
          GUILayout.Width(80f)
        }))
        {
          if (this.behaviorDesignerWindow.IsReferencingTasks() && !flag)
            this.behaviorDesignerWindow.ToggleReferenceTasks();
          this.behaviorDesignerWindow.ToggleReferenceTasks(parentTask, field);
        }
        GUI.backgroundColor = backgroundColor;
        EditorGUILayout.EndHorizontal();
        if (typeof (IList).IsAssignableFrom(field.FieldType))
        {
          if (!(field.GetValue((object) parentTask) is IList list) || list.Count == 0)
          {
            GUILayout.Label("No Tasks Referenced", BehaviorDesignerUtility.TaskInspectorGUIStyle, Array.Empty<GUILayoutOption>());
          }
          else
          {
            for (int index = 0; index < list.Count; ++index)
            {
              if (list[index] is Task)
              {
                EditorGUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
                GUILayout.Label((list[index] as Task).NodeData.NodeDesigner.ToString(), BehaviorDesignerUtility.TaskInspectorGUIStyle, new GUILayoutOption[1]
                {
                  GUILayout.Width(232f)
                });
                if (GUILayout.Button((Texture) BehaviorDesignerUtility.DeleteButtonTexture, BehaviorDesignerUtility.PlainButtonGUIStyle, new GUILayoutOption[1]
                {
                  GUILayout.Width(14f)
                }))
                {
                  this.ReferenceTasks(parentTask, ((list[index] as Task).NodeData.NodeDesigner as NodeDesigner).Task, field);
                  GUI.changed = true;
                }
                GUILayout.Space(3f);
                if (GUILayout.Button((Texture) BehaviorDesignerUtility.IdentifyButtonTexture, BehaviorDesignerUtility.PlainButtonGUIStyle, new GUILayoutOption[1]
                {
                  GUILayout.Width(14f)
                }))
                  this.behaviorDesignerWindow.IdentifyNode((list[index] as Task).NodeData.NodeDesigner as NodeDesigner);
                EditorGUILayout.EndHorizontal();
              }
            }
          }
        }
        else
        {
          EditorGUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
          var task1 = field.GetValue((object)parentTask) as Task;
          GUILayout.Label(task1 == null ? "No Tasks Referenced" : task1.NodeData.NodeDesigner.ToString(), BehaviorDesignerUtility.TaskInspectorGUIStyle, new GUILayoutOption[1]
          {
            GUILayout.Width(232f)
          });
          if (task1 != null)
          {
            if (GUILayout.Button((Texture) BehaviorDesignerUtility.DeleteButtonTexture, BehaviorDesignerUtility.PlainButtonGUIStyle, new GUILayoutOption[1]
            {
              GUILayout.Width(14f)
            }))
            {
              this.ReferenceTasks(parentTask, (Task) null, field);
              GUI.changed = true;
            }
            GUILayout.Space(3f);
            if (GUILayout.Button((Texture) BehaviorDesignerUtility.IdentifyButtonTexture, BehaviorDesignerUtility.PlainButtonGUIStyle, new GUILayoutOption[1]
            {
              GUILayout.Width(14f)
            }))
              this.behaviorDesignerWindow.IdentifyNode(task1.NodeData.NodeDesigner as NodeDesigner);
          }
          EditorGUILayout.EndHorizontal();
        }
      }
    }

    private SharedVariable DrawSharedVariableValue(
      BehaviorSource behaviorSource,
      FieldInfo field,
      GUIContent guiContent,
      Task task,
      SharedVariable sharedVariable,
      bool isReflectionTask,
      bool enabled,
      bool drawWatch)
    {
      if (isReflectionTask)
      {
        if (!field.FieldType.Equals(typeof (SharedVariable)) && sharedVariable == null)
        {
          sharedVariable = Activator.CreateInstance(field.FieldType) as SharedVariable;
          if (TaskUtility.HasAttribute(field, typeof (RequiredFieldAttribute)) || TaskUtility.HasAttribute(field, typeof (SharedRequiredAttribute)))
            sharedVariable.IsShared = true;
          GUI.changed = true;
        }
        if (sharedVariable == null)
        {
          this.mActiveMenuSelectionTask = task;
          this.SecondaryReflectionSelectionCallback((object) null);
          this.ClearInvokeVariablesTask();
          return (SharedVariable) null;
        }
        if (sharedVariable.IsShared)
        {
          GUILayout.Label(guiContent, new GUILayoutOption[1]
          {
            GUILayout.Width(126f)
          });
          string[] names = (string[]) null;
          int globalStartIndex = -1;
          int variablesOfType = FieldInspector.GetVariablesOfType(((object) sharedVariable).GetType().GetProperty("Value").PropertyType, sharedVariable.IsGlobal, sharedVariable.Name, behaviorSource, out names, ref globalStartIndex, false, true);
          Color backgroundColor = GUI.backgroundColor;
          if (variablesOfType == 0 && !TaskUtility.HasAttribute(field, typeof (SharedRequiredAttribute)))
            GUI.backgroundColor = Color.red;
          int num = variablesOfType;
          int index = EditorGUILayout.Popup(variablesOfType, names, EditorStyles.toolbarPopup, Array.Empty<GUILayoutOption>());
          GUI.backgroundColor = backgroundColor;
          if (index != num)
          {
            if (index == 0)
            {
              sharedVariable = !field.FieldType.Equals(typeof (SharedVariable)) ? Activator.CreateInstance(field.FieldType) as SharedVariable : Activator.CreateInstance(FieldInspector.FriendlySharedVariableName(((object) sharedVariable).GetType().GetProperty("Value").PropertyType)) as SharedVariable;
              sharedVariable.IsShared = true;
            }
            else
              sharedVariable = globalStartIndex == -1 || index < globalStartIndex ? behaviorSource.GetVariable(names[index]) : GlobalVariables.Instance.GetVariable(names[index].Substring(8, names[index].Length - 8));
          }
          GUILayout.Space(8f);
        }
        else
        {
          bool drawComponentField;
          if ((drawComponentField = field.Name.Equals("componentName")) || field.Name.Equals("methodName") || field.Name.Equals("fieldName") || field.Name.Equals("propertyName"))
            this.DrawReflectionField(task, guiContent, drawComponentField, field);
          else
            FieldInspector.DrawFields(task, (object) sharedVariable, guiContent);
        }
        if (!TaskUtility.HasAttribute(field, typeof (RequiredFieldAttribute)) && !TaskUtility.HasAttribute(field, typeof (SharedRequiredAttribute)))
          sharedVariable = FieldInspector.DrawSharedVariableToggleSharedButton(sharedVariable);
        else if (!sharedVariable.IsShared)
          sharedVariable.IsShared = true;
      }
      else
        sharedVariable = FieldInspector.DrawSharedVariable(task, guiContent, field, field.FieldType, sharedVariable);
      GUILayout.Space(8f);
      return sharedVariable;
    }

    private void InspectedTaskCallback(object obj)
    {
      if (this.mActiveMenuSelectionTask != null)
      {
        FieldInfo field = ((object) this.mActiveMenuSelectionTask).GetType().GetField("conditionalTask");
        if (obj == null)
        {
          field.SetValue((object) this.mActiveMenuSelectionTask, (object) null);
        }
        else
        {
          System.Type type = (System.Type) obj;
          Task instance1 = Activator.CreateInstance(type, true) as Task;
          field.SetValue((object) this.mActiveMenuSelectionTask, (object) instance1);
          FieldInfo[] serializableFields = TaskUtility.GetSerializableFields(type);
          for (int index = 0; index < serializableFields.Length; ++index)
          {
            if (serializableFields[index].FieldType.IsSubclassOf(typeof (SharedVariable)) && !BehaviorDesignerUtility.HasAttribute(serializableFields[index], typeof (HideInInspector)) && !BehaviorDesignerUtility.HasAttribute(serializableFields[index], typeof (NonSerializedAttribute)) && (!serializableFields[index].IsPrivate && !serializableFields[index].IsFamily || BehaviorDesignerUtility.HasAttribute(serializableFields[index], typeof (SerializeField))))
            {
              SharedVariable instance2 = Activator.CreateInstance(serializableFields[index].FieldType) as SharedVariable;
              instance2.IsShared = false;
              serializableFields[index].SetValue((object) instance1, (object) instance2);
            }
          }
        }
      }
      BehaviorDesignerWindow.instance.SaveBehavior();
    }

    public void SetActiveReferencedTasks(Task referenceTask, FieldInfo fieldInfo)
    {
      this.activeReferenceTask = referenceTask;
      this.activeReferenceTaskFieldInfo = fieldInfo;
    }

    public bool ReferenceTasks(Task referenceTask) => this.ReferenceTasks(this.activeReferenceTask, referenceTask, this.activeReferenceTaskFieldInfo);

    private bool ReferenceTasks(Task sourceTask, Task referenceTask, FieldInfo sourceFieldInfo)
    {
      bool fullSync = false;
      bool doReference = false;
      if (!TaskInspector.ReferenceTasks(sourceTask, referenceTask, sourceFieldInfo, ref fullSync, ref doReference, true, false))
        return false;
      if (referenceTask != null)
      {
        (referenceTask.NodeData.NodeDesigner as NodeDesigner).ShowReferenceIcon = doReference;
        if (fullSync)
          this.PerformFullSync(this.activeReferenceTask);
      }
      return true;
    }

    public static bool ReferenceTasks(
      Task sourceTask,
      Task referenceTask,
      FieldInfo sourceFieldInfo,
      ref bool fullSync,
      ref bool doReference,
      bool synchronize,
      bool unreferenceAll)
    {
      if (referenceTask == null)
      {
        if (sourceFieldInfo.GetValue((object) sourceTask) is Task task)
          (task.NodeData.NodeDesigner as NodeDesigner).ShowReferenceIcon = false;
        sourceFieldInfo.SetValue((object) sourceTask, (object) null);
        return true;
      }
      if (((object) referenceTask).Equals((object) sourceTask) || sourceFieldInfo == (FieldInfo) null || !typeof (IList).IsAssignableFrom(sourceFieldInfo.FieldType) && !sourceFieldInfo.FieldType.IsAssignableFrom(((object) referenceTask).GetType()) || typeof (IList).IsAssignableFrom(sourceFieldInfo.FieldType) && (sourceFieldInfo.FieldType.IsGenericType && !sourceFieldInfo.FieldType.GetGenericArguments()[0].IsAssignableFrom(((object) referenceTask).GetType()) || !sourceFieldInfo.FieldType.IsGenericType && !sourceFieldInfo.FieldType.GetElementType().IsAssignableFrom(((object) referenceTask).GetType())))
        return false;
      if (synchronize && !TaskInspector.IsFieldLinked(sourceFieldInfo))
        synchronize = false;
      if (unreferenceAll)
      {
        sourceFieldInfo.SetValue((object) sourceTask, (object) null);
        (sourceTask.NodeData.NodeDesigner as NodeDesigner).ShowReferenceIcon = false;
      }
      else
      {
        doReference = true;
        bool flag = false;
        if (typeof (IList).IsAssignableFrom(sourceFieldInfo.FieldType))
        {
          Task[] taskArray1 = sourceFieldInfo.GetValue((object) sourceTask) as Task[];
          System.Type type1;
          if (sourceFieldInfo.FieldType.IsArray)
          {
            type1 = sourceFieldInfo.FieldType.GetElementType();
          }
          else
          {
            System.Type type2 = sourceFieldInfo.FieldType;
            while (!type2.IsGenericType)
              type2 = type2.BaseType;
            type1 = type2.GetGenericArguments()[0];
          }
          IList instance1 = Activator.CreateInstance(typeof (List<>).MakeGenericType(type1)) as IList;
          if (taskArray1 != null)
          {
            for (int index = 0; index < taskArray1.Length; ++index)
            {
              if (((object) referenceTask).Equals((object) taskArray1[index]))
                doReference = false;
              else
                instance1.Add((object) taskArray1[index]);
            }
          }
          if (synchronize)
          {
            if (taskArray1 != null && taskArray1.Length > 0)
            {
              for (int index = 0; index < taskArray1.Length; ++index)
              {
                TaskInspector.ReferenceTasks(taskArray1[index], referenceTask, ((object) taskArray1[index]).GetType().GetField(sourceFieldInfo.Name), ref flag, ref doReference, false, false);
                if (doReference)
                  TaskInspector.ReferenceTasks(referenceTask, taskArray1[index], ((object) referenceTask).GetType().GetField(sourceFieldInfo.Name), ref flag, ref doReference, false, false);
              }
            }
            else if (doReference)
            {
              FieldInfo field = ((object) referenceTask).GetType().GetField(sourceFieldInfo.Name);
              if (field != (FieldInfo) null && field.GetValue((object) referenceTask) is Task[] taskArray2)
              {
                for (int index = 0; index < taskArray2.Length; ++index)
                {
                  instance1.Add((object) taskArray2[index]);
                  (taskArray2[index].NodeData.NodeDesigner as NodeDesigner).ShowReferenceIcon = true;
                  TaskInspector.ReferenceTasks(taskArray2[index], sourceTask, ((object) taskArray2[index]).GetType().GetField(sourceFieldInfo.Name), ref doReference, ref flag, false, false);
                }
                doReference = true;
              }
            }
            TaskInspector.ReferenceTasks(referenceTask, sourceTask, ((object) referenceTask).GetType().GetField(sourceFieldInfo.Name), ref flag, ref doReference, false, !doReference);
          }
          if (doReference)
            instance1.Add((object) referenceTask);
          if (sourceFieldInfo.FieldType.IsArray)
          {
            Array instance2 = Array.CreateInstance(sourceFieldInfo.FieldType.GetElementType(), instance1.Count);
            instance1.CopyTo(instance2, 0);
            sourceFieldInfo.SetValue((object) sourceTask, (object) instance2);
          }
          else
            sourceFieldInfo.SetValue((object) sourceTask, (object) instance1);
        }
        else
        {
          Task sourceTask1 = sourceFieldInfo.GetValue((object) sourceTask) as Task;
          doReference = !((object) referenceTask).Equals((object) sourceTask1);
          if (TaskInspector.IsFieldLinked(sourceFieldInfo) && sourceTask1 != null)
            TaskInspector.ReferenceTasks(sourceTask1, sourceTask, ((object) sourceTask1).GetType().GetField(sourceFieldInfo.Name), ref flag, ref doReference, false, true);
          if (synchronize)
            TaskInspector.ReferenceTasks(referenceTask, sourceTask, ((object) referenceTask).GetType().GetField(sourceFieldInfo.Name), ref flag, ref doReference, false, !doReference);
          sourceFieldInfo.SetValue((object) sourceTask, !doReference ? (object) (Task) null : (object) referenceTask);
        }
        if (synchronize)
          (referenceTask.NodeData.NodeDesigner as NodeDesigner).ShowReferenceIcon = doReference;
        fullSync = doReference && synchronize;
      }
      return true;
    }

    public bool IsActiveTaskArray() => this.activeReferenceTaskFieldInfo.FieldType.IsArray;

    public bool IsActiveTaskNull() => this.activeReferenceTaskFieldInfo.GetValue((object) this.activeReferenceTask) == null;

    public static bool IsFieldLinked(FieldInfo field) => BehaviorDesignerUtility.HasAttribute(field, typeof (LinkedTaskAttribute));

    public static List<Task> GetReferencedTasks(Task task)
    {
      List<Task> taskList = new List<Task>();
      FieldInfo[] serializableFields = TaskUtility.GetSerializableFields(((object) task).GetType());
      for (int index1 = 0; index1 < serializableFields.Length; ++index1)
      {
        if (!serializableFields[index1].IsPrivate && !serializableFields[index1].IsFamily || BehaviorDesignerUtility.HasAttribute(serializableFields[index1], typeof (SerializeField)))
        {
          if (typeof (IList).IsAssignableFrom(serializableFields[index1].FieldType) && (typeof (Task).IsAssignableFrom(serializableFields[index1].FieldType.GetElementType()) || serializableFields[index1].FieldType.IsGenericType && typeof (Task).IsAssignableFrom(serializableFields[index1].FieldType.GetGenericArguments()[0])))
          {
            if (serializableFields[index1].GetValue((object) task) is Task[] taskArray)
            {
              for (int index2 = 0; index2 < taskArray.Length; ++index2)
                taskList.Add(taskArray[index2]);
            }
          }
          else if (serializableFields[index1].FieldType.IsSubclassOf(typeof (Task)) && serializableFields[index1].GetValue((object) task) != null)
            taskList.Add(serializableFields[index1].GetValue((object) task) as Task);
        }
      }
      return taskList.Count > 0 ? taskList : (List<Task>) null;
    }

    private void PerformFullSync(Task task)
    {
      List<Task> referencedTasks = TaskInspector.GetReferencedTasks(task);
      if (referencedTasks == null)
        return;
      FieldInfo[] serializableFields = TaskUtility.GetSerializableFields(((object) task).GetType());
      for (int index1 = 0; index1 < serializableFields.Length; ++index1)
      {
        if (!TaskInspector.IsFieldLinked(serializableFields[index1]))
        {
          for (int index2 = 0; index2 < referencedTasks.Count; ++index2)
          {
            FieldInfo field;
            if ((field = ((object) referencedTasks[index2]).GetType().GetField(serializableFields[index1].Name)) != (FieldInfo) null)
              field.SetValue((object) referencedTasks[index2], serializableFields[index1].GetValue((object) task));
          }
        }
      }
    }

    public static void OpenInFileEditor(object task)
    {
      MonoScript[] objectsOfTypeAll = (MonoScript[]) Resources.FindObjectsOfTypeAll(typeof (MonoScript));
      for (int index = 0; index < objectsOfTypeAll.Length; ++index)
      {
        if ((UnityEngine.Object) objectsOfTypeAll[index] != (UnityEngine.Object) null && objectsOfTypeAll[index].GetClass() != (System.Type) null && objectsOfTypeAll[index].GetClass().Equals(task.GetType()))
        {
          AssetDatabase.OpenAsset((UnityEngine.Object) objectsOfTypeAll[index]);
          break;
        }
      }
    }

    public static void SelectInProject(object task)
    {
      MonoScript[] objectsOfTypeAll = (MonoScript[]) Resources.FindObjectsOfTypeAll(typeof (MonoScript));
      for (int index = 0; index < objectsOfTypeAll.Length; ++index)
      {
        if ((UnityEngine.Object) objectsOfTypeAll[index] != (UnityEngine.Object) null && objectsOfTypeAll[index].GetClass() != (System.Type) null && objectsOfTypeAll[index].GetClass().Equals(task.GetType()))
        {
          Selection.activeObject = (UnityEngine.Object) objectsOfTypeAll[index];
          break;
        }
      }
    }

    private void ResetTask(object task)
    {
      (task as Task).OnReset();
      List<System.Type> baseClasses = FieldInspector.GetBaseClasses(task.GetType());
      BindingFlags bindingAttr = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
      for (int index1 = baseClasses.Count - 1; index1 > -1; --index1)
      {
        FieldInfo[] fields = baseClasses[index1].GetFields(bindingAttr);
        for (int index2 = 0; index2 < fields.Length; ++index2)
        {
          if (typeof (SharedVariable).IsAssignableFrom(fields[index2].FieldType))
          {
            SharedVariable sharedVariable = fields[index2].GetValue(task) as SharedVariable;
            if (TaskUtility.HasAttribute(fields[index2], typeof (RequiredFieldAttribute)) && sharedVariable != null && !sharedVariable.IsShared)
              sharedVariable.IsShared = true;
          }
        }
      }
      this.behaviorDesignerWindow.SaveBehavior();
    }

    private void AddColorMenuItem(ref GenericMenu menu, Task task, string color, int index) => menu.AddItem(new GUIContent(color), task.NodeData.ColorIndex == index, SetTaskColor, (object) new TaskInspector.TaskColor(task, index));

    private void SetTaskColor(object value)
    {
      TaskInspector.TaskColor taskColor = value as TaskInspector.TaskColor;
      if (taskColor.task.NodeData.ColorIndex == taskColor.colorIndex)
        return;
      taskColor.task.NodeData.ColorIndex = taskColor.colorIndex;
      BehaviorDesignerWindow.instance.SaveBehavior();
    }

    private void OpenHelpURL(Task task)
    {
      HelpURLAttribute[] customAttributes;
      if ((customAttributes = ((object) task).GetType().GetCustomAttributes(typeof (HelpURLAttribute), false) as HelpURLAttribute[]).Length <= 0)
        return;
      Application.OpenURL(customAttributes[0].URL);
    }

    private bool IsReflectionTask(System.Type type) => this.IsInvokeMethodTask(type) || this.IsFieldReflectionTask(type) || this.IsPropertyReflectionTask(type);

    private bool IsInvokeMethodTask(System.Type type) => TaskUtility.CompareType(type, "BehaviorDesigner.Runtime.Tasks.InvokeMethod");

    private bool IsFieldReflectionTask(System.Type type) => TaskUtility.CompareType(type, "BehaviorDesigner.Runtime.Tasks.GetFieldValue") || TaskUtility.CompareType(type, "BehaviorDesigner.Runtime.Tasks.SetFieldValue") || TaskUtility.CompareType(type, "BehaviorDesigner.Runtime.Tasks.CompareFieldValue");

    private bool IsPropertyReflectionTask(System.Type type) => TaskUtility.CompareType(type, "BehaviorDesigner.Runtime.Tasks.GetPropertyValue") || TaskUtility.CompareType(type, "BehaviorDesigner.Runtime.Tasks.SetPropertyValue") || TaskUtility.CompareType(type, "BehaviorDesigner.Runtime.Tasks.ComparePropertyValue");

    private bool IsReflectionGetterTask(System.Type type) => TaskUtility.CompareType(type, "BehaviorDesigner.Runtime.Tasks.GetFieldValue") || TaskUtility.CompareType(type, "BehaviorDesigner.Runtime.Tasks.GetPropertyValue");

    private void DrawReflectionField(
      Task task,
      GUIContent guiContent,
      bool drawComponentField,
      FieldInfo field)
    {
      SharedVariable sharedVariable1 = ((object) task).GetType().GetField("targetGameObject").GetValue((object) task) as SharedVariable;
      if (drawComponentField)
      {
        GUILayout.Label(guiContent, new GUILayoutOption[1]
        {
          GUILayout.Width(146f)
        });
        SharedVariable sharedVariable2 = field.GetValue((object) task) as SharedVariable;
        string empty = string.Empty;
        string str;
        if (sharedVariable2 == null || string.IsNullOrEmpty((string) sharedVariable2.GetValue()))
        {
          str = "Select";
        }
        else
        {
          string[] strArray = ((string) sharedVariable2.GetValue()).Split('.');
          str = strArray[strArray.Length - 1];
        }
        if (GUILayout.Button(str, EditorStyles.toolbarPopup, new GUILayoutOption[1]
        {
          GUILayout.Width(92f)
        }))
        {
          GenericMenu genericMenu = new GenericMenu();
          // ISSUE: method pointer
          genericMenu.AddItem(new GUIContent("None"), string.IsNullOrEmpty((string) sharedVariable2.GetValue()), ComponentSelectionCallback, (object) null);
          GameObject gameObject = (GameObject) null;
          if (sharedVariable1 == null || (UnityEngine.Object) sharedVariable1.GetValue() == (UnityEngine.Object) null)
          {
            if ((UnityEngine.Object) task.Owner != (UnityEngine.Object) null)
              gameObject = ((Component) task.Owner).gameObject;
          }
          else
            gameObject = (GameObject) sharedVariable1.GetValue();
          if ((UnityEngine.Object) gameObject != (UnityEngine.Object) null)
          {
            Component[] components = gameObject.GetComponents<Component>();
            for (int index = 0; index < components.Length; ++index)
            {
              // ISSUE: method pointer
              genericMenu.AddItem(new GUIContent(((object) components[index]).GetType().Name), ((object) components[index]).GetType().FullName.Equals((string) sharedVariable2.GetValue()), ComponentSelectionCallback, (object) ((object) components[index]).GetType().FullName);
            }
            genericMenu.ShowAsContext();
            this.mActiveMenuSelectionTask = task;
          }
        }
      }
      else
      {
        GUILayout.Label(guiContent, new GUILayoutOption[1]
        {
          GUILayout.Width(146f)
        });
        SharedVariable sharedVariable3 = ((object) task).GetType().GetField("componentName").GetValue((object) task) as SharedVariable;
        SharedVariable sharedVariable4 = field.GetValue((object) task) as SharedVariable;
        string empty = string.Empty;
        if (GUILayout.Button(sharedVariable3 == null || string.IsNullOrEmpty((string) sharedVariable3.GetValue()) ? "Component Required" : (!string.IsNullOrEmpty((string) sharedVariable4.GetValue()) ? (string) sharedVariable4.GetValue() : "Select"), EditorStyles.toolbarPopup, new GUILayoutOption[1]
        {
          GUILayout.Width(92f)
        }) && !string.IsNullOrEmpty((string) sharedVariable3.GetValue()))
        {
          GenericMenu genericMenu = new GenericMenu();
          // ISSUE: method pointer
          genericMenu.AddItem(new GUIContent("None"), string.IsNullOrEmpty((string) sharedVariable4.GetValue()), SecondaryReflectionSelectionCallback, (object) null);
          GameObject gameObject = (GameObject) null;
          if (sharedVariable1 == null || (UnityEngine.Object) sharedVariable1.GetValue() == (UnityEngine.Object) null)
          {
            if ((UnityEngine.Object) task.Owner != (UnityEngine.Object) null)
              gameObject = ((Component) task.Owner).gameObject;
          }
          else
            gameObject = (GameObject) sharedVariable1.GetValue();
          if ((UnityEngine.Object) gameObject != (UnityEngine.Object) null)
          {
            Component component = gameObject.GetComponent(TaskUtility.GetTypeWithinAssembly((string) sharedVariable3.GetValue()));
            List<System.Type> sharedVariableTypes = VariableInspector.FindAllSharedVariableTypes(false);
            if (this.IsInvokeMethodTask(((object) task).GetType()))
            {
              MethodInfo[] methods = ((object) component).GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
              for (int index1 = 0; index1 < methods.Length; ++index1)
              {
                if (!methods[index1].IsSpecialName && !methods[index1].IsGenericMethod && methods[index1].GetParameters().Length <= 4)
                {
                  ParameterInfo[] parameters = methods[index1].GetParameters();
                  bool flag = true;
                  for (int index2 = 0; index2 < parameters.Length; ++index2)
                  {
                    if (!this.SharedVariableTypeExists(sharedVariableTypes, parameters[index2].ParameterType))
                    {
                      flag = false;
                      break;
                    }
                  }
                  if (flag && (methods[index1].ReturnType.Equals(typeof (void)) || this.SharedVariableTypeExists(sharedVariableTypes, methods[index1].ReturnType)))
                  {
                    // ISSUE: method pointer
                    genericMenu.AddItem(new GUIContent(methods[index1].Name), methods[index1].Name.Equals((string) sharedVariable4.GetValue()), SecondaryReflectionSelectionCallback, (object) methods[index1]);
                  }
                }
              }
            }
            else if (this.IsFieldReflectionTask(((object) task).GetType()))
            {
              FieldInfo[] fields = ((object) component).GetType().GetFields(BindingFlags.Instance | BindingFlags.Public);
              for (int index = 0; index < fields.Length; ++index)
              {
                if (!fields[index].IsSpecialName && this.SharedVariableTypeExists(sharedVariableTypes, fields[index].FieldType))
                {
                  // ISSUE: method pointer
                  genericMenu.AddItem(new GUIContent(fields[index].Name), fields[index].Name.Equals((string) sharedVariable4.GetValue()), SecondaryReflectionSelectionCallback, (object) fields[index]);
                }
              }
            }
            else
            {
              PropertyInfo[] properties = ((object) component).GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
              for (int index = 0; index < properties.Length; ++index)
              {
                if (!properties[index].IsSpecialName && this.SharedVariableTypeExists(sharedVariableTypes, properties[index].PropertyType))
                {
                  // ISSUE: method pointer
                  genericMenu.AddItem(new GUIContent(properties[index].Name), properties[index].Name.Equals((string) sharedVariable4.GetValue()), SecondaryReflectionSelectionCallback, (object) properties[index]);
                }
              }
            }
            genericMenu.ShowAsContext();
            this.mActiveMenuSelectionTask = task;
          }
        }
      }
      GUILayout.Space(8f);
    }

    private void ComponentSelectionCallback(object obj)
    {
      if (this.mActiveMenuSelectionTask != null)
      {
        FieldInfo field1 = ((object) this.mActiveMenuSelectionTask).GetType().GetField("componentName");
        SharedVariable instance1 = Activator.CreateInstance(TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.SharedString")) as SharedVariable;
        if (obj == null)
        {
          field1.SetValue((object) this.mActiveMenuSelectionTask, (object) instance1);
          SharedVariable instance2 = Activator.CreateInstance(TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.SharedString")) as SharedVariable;
          FieldInfo fieldInfo;
          if (this.IsInvokeMethodTask(((object) this.mActiveMenuSelectionTask).GetType()))
          {
            fieldInfo = ((object) this.mActiveMenuSelectionTask).GetType().GetField("methodName");
            this.ClearInvokeVariablesTask();
          }
          else
            fieldInfo = !this.IsFieldReflectionTask(((object) this.mActiveMenuSelectionTask).GetType()) ? ((object) this.mActiveMenuSelectionTask).GetType().GetField("propertyName") : ((object) this.mActiveMenuSelectionTask).GetType().GetField("fieldName");
          fieldInfo.SetValue((object) this.mActiveMenuSelectionTask, (object) instance2);
        }
        else
        {
          string str = (string) obj;
          SharedVariable sharedVariable = field1.GetValue((object) this.mActiveMenuSelectionTask) as SharedVariable;
          if (!str.Equals((string) sharedVariable.GetValue()))
          {
            FieldInfo field2;
            FieldInfo field3;
            if (this.IsInvokeMethodTask(((object) this.mActiveMenuSelectionTask).GetType()))
            {
              field2 = ((object) this.mActiveMenuSelectionTask).GetType().GetField("methodName");
              for (int index = 0; index < 4; ++index)
                ((object) this.mActiveMenuSelectionTask).GetType().GetField("parameter" + (object) (index + 1)).SetValue((object) this.mActiveMenuSelectionTask, (object) null);
              field3 = ((object) this.mActiveMenuSelectionTask).GetType().GetField("storeResult");
            }
            else if (this.IsFieldReflectionTask(((object) this.mActiveMenuSelectionTask).GetType()))
            {
              field2 = ((object) this.mActiveMenuSelectionTask).GetType().GetField("fieldName");
              field3 = ((object) this.mActiveMenuSelectionTask).GetType().GetField("fieldValue");
              if (field3 == (FieldInfo) null)
                field3 = ((object) this.mActiveMenuSelectionTask).GetType().GetField("compareValue");
            }
            else
            {
              field2 = ((object) this.mActiveMenuSelectionTask).GetType().GetField("propertyName");
              field3 = ((object) this.mActiveMenuSelectionTask).GetType().GetField("propertyValue");
              if (field3 == (FieldInfo) null)
                field3 = ((object) this.mActiveMenuSelectionTask).GetType().GetField("compareValue");
            }
            field2.SetValue((object) this.mActiveMenuSelectionTask, (object) instance1);
            field3.SetValue((object) this.mActiveMenuSelectionTask, (object) null);
          }
          SharedVariable instance3 = Activator.CreateInstance(TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.SharedString")) as SharedVariable;
          instance3.SetValue((object) str);
          field1.SetValue((object) this.mActiveMenuSelectionTask, (object) instance3);
        }
      }
      BehaviorDesignerWindow.instance.SaveBehavior();
    }

    private void SecondaryReflectionSelectionCallback(object obj)
    {
      if (this.mActiveMenuSelectionTask != null)
      {
        SharedVariable instance1 = Activator.CreateInstance(TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.SharedString")) as SharedVariable;
        FieldInfo fieldInfo1;
        if (this.IsInvokeMethodTask(((object) this.mActiveMenuSelectionTask).GetType()))
        {
          this.ClearInvokeVariablesTask();
          fieldInfo1 = ((object) this.mActiveMenuSelectionTask).GetType().GetField("methodName");
        }
        else
          fieldInfo1 = !this.IsFieldReflectionTask(((object) this.mActiveMenuSelectionTask).GetType()) ? ((object) this.mActiveMenuSelectionTask).GetType().GetField("propertyName") : ((object) this.mActiveMenuSelectionTask).GetType().GetField("fieldName");
        if (obj == null)
          fieldInfo1.SetValue((object) this.mActiveMenuSelectionTask, (object) instance1);
        else if (this.IsInvokeMethodTask(((object) this.mActiveMenuSelectionTask).GetType()))
        {
          MethodInfo methodInfo = (MethodInfo) obj;
          instance1.SetValue((object) methodInfo.Name);
          fieldInfo1.SetValue((object) this.mActiveMenuSelectionTask, (object) instance1);
          ParameterInfo[] parameters = methodInfo.GetParameters();
          for (int index = 0; index < 4; ++index)
          {
            FieldInfo field = ((object) this.mActiveMenuSelectionTask).GetType().GetField("parameter" + (object) (index + 1));
            if (index < parameters.Length)
            {
              SharedVariable instance2 = Activator.CreateInstance(FieldInspector.FriendlySharedVariableName(parameters[index].ParameterType)) as SharedVariable;
              field.SetValue((object) this.mActiveMenuSelectionTask, (object) instance2);
            }
            else
              field.SetValue((object) this.mActiveMenuSelectionTask, (object) null);
          }
          if (!methodInfo.ReturnType.Equals(typeof (void)))
          {
            FieldInfo field = ((object) this.mActiveMenuSelectionTask).GetType().GetField("storeResult");
            SharedVariable instance3 = Activator.CreateInstance(FieldInspector.FriendlySharedVariableName(methodInfo.ReturnType)) as SharedVariable;
            instance3.IsShared = true;
            field.SetValue((object) this.mActiveMenuSelectionTask, (object) instance3);
          }
        }
        else if (this.IsFieldReflectionTask(((object) this.mActiveMenuSelectionTask).GetType()))
        {
          FieldInfo fieldInfo2 = (FieldInfo) obj;
          instance1.SetValue((object) fieldInfo2.Name);
          fieldInfo1.SetValue((object) this.mActiveMenuSelectionTask, (object) instance1);
          FieldInfo field = ((object) this.mActiveMenuSelectionTask).GetType().GetField("fieldValue");
          if (field == (FieldInfo) null)
            field = ((object) this.mActiveMenuSelectionTask).GetType().GetField("compareValue");
          SharedVariable instance4 = Activator.CreateInstance(FieldInspector.FriendlySharedVariableName(fieldInfo2.FieldType)) as SharedVariable;
          instance4.IsShared = this.IsReflectionGetterTask(((object) this.mActiveMenuSelectionTask).GetType());
          field.SetValue((object) this.mActiveMenuSelectionTask, (object) instance4);
        }
        else
        {
          PropertyInfo propertyInfo = (PropertyInfo) obj;
          instance1.SetValue((object) propertyInfo.Name);
          fieldInfo1.SetValue((object) this.mActiveMenuSelectionTask, (object) instance1);
          FieldInfo field = ((object) this.mActiveMenuSelectionTask).GetType().GetField("propertyValue");
          if (field == (FieldInfo) null)
            field = ((object) this.mActiveMenuSelectionTask).GetType().GetField("compareValue");
          SharedVariable instance5 = Activator.CreateInstance(FieldInspector.FriendlySharedVariableName(propertyInfo.PropertyType)) as SharedVariable;
          instance5.IsShared = this.IsReflectionGetterTask(((object) this.mActiveMenuSelectionTask).GetType());
          field.SetValue((object) this.mActiveMenuSelectionTask, (object) instance5);
        }
      }
      BehaviorDesignerWindow.instance.SaveBehavior();
    }

    private void ClearInvokeVariablesTask()
    {
      for (int index = 0; index < 4; ++index)
        ((object) this.mActiveMenuSelectionTask).GetType().GetField("parameter" + (object) (index + 1)).SetValue((object) this.mActiveMenuSelectionTask, (object) null);
      ((object) this.mActiveMenuSelectionTask).GetType().GetField("storeResult").SetValue((object) this.mActiveMenuSelectionTask, (object) null);
    }

    private bool CanDrawReflectedField(object task, FieldInfo field)
    {
      if (!field.Name.Contains("parameter") && !field.Name.Contains("storeResult") && !field.Name.Contains("fieldValue") && !field.Name.Contains("propertyValue") && !field.Name.Contains("compareValue"))
        return true;
      if (this.IsInvokeMethodTask(task.GetType()))
      {
        if (field.Name.Contains("parameter"))
          return task.GetType().GetField(field.Name).GetValue(task) != null;
        MethodInfo invokeMethodInfo;
        if ((invokeMethodInfo = this.GetInvokeMethodInfo(task)) == (MethodInfo) null)
          return false;
        return !field.Name.Equals("storeResult") || !invokeMethodInfo.ReturnType.Equals(typeof (void));
      }
      return this.IsFieldReflectionTask(task.GetType()) ? task.GetType().GetField("fieldName").GetValue(task) is SharedVariable sharedVariable1 && !string.IsNullOrEmpty((string) sharedVariable1.GetValue()) : task.GetType().GetField("propertyName").GetValue(task) is SharedVariable sharedVariable2 && !string.IsNullOrEmpty((string) sharedVariable2.GetValue());
    }

    private string InvokeParameterName(object task, FieldInfo field)
    {
      if (!field.Name.Contains("parameter"))
        return field.Name;
      MethodInfo invokeMethodInfo;
      if ((invokeMethodInfo = this.GetInvokeMethodInfo(task)) == (MethodInfo) null)
        return field.Name;
      ParameterInfo[] parameters = invokeMethodInfo.GetParameters();
      int index = int.Parse(field.Name.Substring(9)) - 1;
      return index < parameters.Length ? parameters[index].Name : field.Name;
    }

    private MethodInfo GetInvokeMethodInfo(object task)
    {
      SharedVariable sharedVariable1 = task.GetType().GetField("targetGameObject").GetValue(task) as SharedVariable;
      GameObject gameObject = (GameObject) null;
      if (sharedVariable1 == null || (UnityEngine.Object) sharedVariable1.GetValue() == (UnityEngine.Object) null)
      {
        if ((UnityEngine.Object) (task as Task).Owner != (UnityEngine.Object) null)
          gameObject = ((Component) (task as Task).Owner).gameObject;
      }
      else
        gameObject = (GameObject) sharedVariable1.GetValue();
      if ((UnityEngine.Object) gameObject == (UnityEngine.Object) null)
        return (MethodInfo) null;
      if (!(task.GetType().GetField("componentName").GetValue(task) is SharedVariable sharedVariable2) || string.IsNullOrEmpty((string) sharedVariable2.GetValue()))
        return (MethodInfo) null;
      if (!(task.GetType().GetField("methodName").GetValue(task) is SharedVariable sharedVariable3) || string.IsNullOrEmpty((string) sharedVariable3.GetValue()))
        return (MethodInfo) null;
      List<System.Type> typeList = new List<System.Type>();
      for (int index = 0; index < 4 && task.GetType().GetField("parameter" + (object) (index + 1)).GetValue(task) is SharedVariable sharedVariable4; ++index)
        typeList.Add(((object) sharedVariable4).GetType().GetProperty("Value").PropertyType);
      Component component = gameObject.GetComponent(TaskUtility.GetTypeWithinAssembly((string) sharedVariable2.GetValue()));
      return (UnityEngine.Object) component == (UnityEngine.Object) null ? (MethodInfo) null : ((object) component).GetType().GetMethod((string) sharedVariable3.GetValue(), typeList.ToArray());
    }

    private bool SharedVariableTypeExists(List<System.Type> sharedVariableTypes, System.Type type)
    {
      System.Type type1 = FieldInspector.FriendlySharedVariableName(type);
      for (int index = 0; index < sharedVariableTypes.Count; ++index)
      {
        if (type1.IsAssignableFrom(sharedVariableTypes[index]))
          return true;
      }
      return false;
    }

    private class TaskColor
    {
      public Task task;
      public int colorIndex;

      public TaskColor(Task task, int colorIndex)
      {
        this.task = task;
        this.colorIndex = colorIndex;
      }
    }
  }
}
