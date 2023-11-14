// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.VariableSynchronizerInspector
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
  [CustomEditor(typeof (VariableSynchronizer))]
  public class VariableSynchronizerInspector : UnityEditor.Editor
  {
    [SerializeField]
    private VariableSynchronizerInspector.Synchronizer sharedVariableSynchronizer = new VariableSynchronizerInspector.Synchronizer();
    [SerializeField]
    private string sharedVariableValueTypeName;
    private System.Type sharedVariableValueType;
    [SerializeField]
    private VariableSynchronizer.SynchronizationType synchronizationType;
    [SerializeField]
    private bool setVariable;
    [SerializeField]
    private VariableSynchronizerInspector.Synchronizer targetSynchronizer;
    private Action<VariableSynchronizerInspector.Synchronizer, System.Type> thirdPartySynchronizer;
    private System.Type playMakerSynchronizationType;
    private System.Type uFrameSynchronizationType;

    public virtual void OnInspectorGUI()
    {
      VariableSynchronizer target = this.target as VariableSynchronizer;
      if ((UnityEngine.Object) target == (UnityEngine.Object) null)
        return;
      GUILayout.Space(5f);
      target.UpdateInterval = (UpdateIntervalType) EditorGUILayout.EnumPopup("Update Interval", (Enum) (object) target.UpdateInterval, Array.Empty<GUILayoutOption>());
      if (target.UpdateInterval == UpdateIntervalType.SpecifySeconds)
        target.UpdateIntervalSeconds = EditorGUILayout.FloatField("Seconds", target.UpdateIntervalSeconds, Array.Empty<GUILayoutOption>());
      GUILayout.Space(5f);
      GUI.enabled = !Application.isPlaying;
      this.DrawSharedVariableSynchronizer(this.sharedVariableSynchronizer, (System.Type) null);
      if (string.IsNullOrEmpty(this.sharedVariableSynchronizer.targetName))
      {
        this.DrawSynchronizedVariables(target);
      }
      else
      {
        EditorGUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        EditorGUILayout.LabelField("Direction", new GUILayoutOption[1]
        {
          GUILayout.MaxWidth(146f)
        });
        if (GUILayout.Button((Texture) BehaviorDesignerUtility.LoadTexture(!this.setVariable ? "RightArrowButton.png" : "LeftArrowButton.png", obj: ((UnityEngine.Object) this)), BehaviorDesignerUtility.ButtonGUIStyle, new GUILayoutOption[1]
        {
          GUILayout.Width(22f)
        }))
          this.setVariable = !this.setVariable;
        EditorGUILayout.EndHorizontal();
        EditorGUI.BeginChangeCheck();
        this.synchronizationType = (VariableSynchronizer.SynchronizationType) EditorGUILayout.EnumPopup("Type", (Enum) (object) this.synchronizationType, Array.Empty<GUILayoutOption>());
        if (EditorGUI.EndChangeCheck())
          this.targetSynchronizer = new VariableSynchronizerInspector.Synchronizer();
        if (this.targetSynchronizer == null)
          this.targetSynchronizer = new VariableSynchronizerInspector.Synchronizer();
        if (this.sharedVariableValueType == (System.Type) null && !string.IsNullOrEmpty(this.sharedVariableValueTypeName))
          this.sharedVariableValueType = TaskUtility.GetTypeWithinAssembly(this.sharedVariableValueTypeName);
        switch ((int) this.synchronizationType)
        {
          case 0:
            this.DrawSharedVariableSynchronizer(this.targetSynchronizer, this.sharedVariableValueType);
            break;
          case 1:
            this.DrawPropertySynchronizer(this.targetSynchronizer, this.sharedVariableValueType);
            break;
          case 2:
            this.DrawAnimatorSynchronizer(this.targetSynchronizer);
            break;
          case 3:
            this.DrawPlayMakerSynchronizer(this.targetSynchronizer, this.sharedVariableValueType);
            break;
          case 4:
            this.DrawuFrameSynchronizer(this.targetSynchronizer, this.sharedVariableValueType);
            break;
        }
        if (string.IsNullOrEmpty(this.targetSynchronizer.targetName))
          GUI.enabled = false;
        if (GUILayout.Button("Add", Array.Empty<GUILayoutOption>()))
        {
          VariableSynchronizer.SynchronizedVariable synchronizedVariable = new VariableSynchronizer.SynchronizedVariable(this.synchronizationType, this.setVariable, this.sharedVariableSynchronizer.component as Behavior, this.sharedVariableSynchronizer.targetName, this.sharedVariableSynchronizer.global, this.targetSynchronizer.component, this.targetSynchronizer.targetName, this.targetSynchronizer.global);
          target.SynchronizedVariables.Add(synchronizedVariable);
          BehaviorDesignerUtility.SetObjectDirty((UnityEngine.Object) target);
          this.sharedVariableSynchronizer = new VariableSynchronizerInspector.Synchronizer();
          this.targetSynchronizer = new VariableSynchronizerInspector.Synchronizer();
        }
        GUI.enabled = true;
        this.DrawSynchronizedVariables(target);
      }
    }

    public static void DrawComponentSelector(
      VariableSynchronizerInspector.Synchronizer synchronizer,
      System.Type componentType,
      VariableSynchronizerInspector.ComponentListType listType)
    {
      bool flag = false;
      EditorGUI.BeginChangeCheck();
      synchronizer.gameObject = EditorGUILayout.ObjectField("GameObject", (UnityEngine.Object) synchronizer.gameObject, typeof (GameObject), true, Array.Empty<GUILayoutOption>()) as GameObject;
      if (EditorGUI.EndChangeCheck())
        flag = true;
      if ((UnityEngine.Object) synchronizer.gameObject == (UnityEngine.Object) null)
        GUI.enabled = false;
      switch (listType)
      {
        case VariableSynchronizerInspector.ComponentListType.Instant:
          if (!flag)
            break;
          if ((UnityEngine.Object) synchronizer.gameObject != (UnityEngine.Object) null)
          {
            synchronizer.component = synchronizer.gameObject.GetComponent(componentType);
            break;
          }
          synchronizer.component = (Component) null;
          break;
        case VariableSynchronizerInspector.ComponentListType.Popup:
          int num1 = 0;
          List<string> stringList = new List<string>();
          Component[] componentArray = (Component[]) null;
          stringList.Add("None");
          if ((UnityEngine.Object) synchronizer.gameObject != (UnityEngine.Object) null)
          {
            componentArray = synchronizer.gameObject.GetComponents(componentType);
            for (int index1 = 0; index1 < componentArray.Length; ++index1)
            {
              if (((object) componentArray[index1]).Equals((object) synchronizer.component))
                num1 = stringList.Count;
              string str = BehaviorDesignerUtility.SplitCamelCase(((object) componentArray[index1]).GetType().Name);
              int num2 = 0;
              for (int index2 = 0; index2 < stringList.Count; ++index2)
              {
                if (stringList[index1].Equals(str))
                  ++num2;
              }
              if (num2 > 0)
                str = str + " " + (object) num2;
              stringList.Add(str);
            }
          }
          EditorGUI.BeginChangeCheck();
          int num3 = EditorGUILayout.Popup("Component", num1, stringList.ToArray(), Array.Empty<GUILayoutOption>());
          if (!EditorGUI.EndChangeCheck())
            break;
          if (num3 != 0)
          {
            synchronizer.component = componentArray[num3 - 1];
            break;
          }
          synchronizer.component = (Component) null;
          break;
        case VariableSynchronizerInspector.ComponentListType.BehaviorDesignerGroup:
          if (!((UnityEngine.Object) synchronizer.gameObject != (UnityEngine.Object) null))
            break;
          Behavior[] components = synchronizer.gameObject.GetComponents<Behavior>();
          if (components != null && components.Length > 1)
            synchronizer.componentGroup = EditorGUILayout.IntField("Behavior Tree Group", synchronizer.componentGroup, Array.Empty<GUILayoutOption>());
          synchronizer.component = (Component) VariableSynchronizerInspector.GetBehaviorWithGroup(components, synchronizer.componentGroup);
          break;
      }
    }

    private bool DrawSharedVariableSynchronizer(
      VariableSynchronizerInspector.Synchronizer synchronizer,
      System.Type valueType)
    {
      VariableSynchronizerInspector.DrawComponentSelector(synchronizer, typeof (Behavior), VariableSynchronizerInspector.ComponentListType.BehaviorDesignerGroup);
      int num = 0;
      int globalStartIndex = -1;
      string[] names = (string[]) null;
      if ((UnityEngine.Object) synchronizer.component != (UnityEngine.Object) null)
      {
        Behavior component = synchronizer.component as Behavior;
        num = FieldInspector.GetVariablesOfType(valueType, synchronizer.global, synchronizer.targetName, component.GetBehaviorSource(), out names, ref globalStartIndex, valueType == (System.Type) null, false);
      }
      else
        names = new string[1]{ "None" };
      EditorGUI.BeginChangeCheck();
      int index = EditorGUILayout.Popup("Shared Variable", num, names, Array.Empty<GUILayoutOption>());
      if (EditorGUI.EndChangeCheck())
      {
        if (index != 0)
        {
          if (globalStartIndex != -1 && index >= globalStartIndex)
          {
            synchronizer.targetName = names[index].Substring(8, names[index].Length - 8);
            synchronizer.global = true;
          }
          else
          {
            synchronizer.targetName = names[index];
            synchronizer.global = false;
          }
          if (valueType == (System.Type) null)
          {
            this.sharedVariableValueTypeName = (!synchronizer.global ? (object) (synchronizer.component as Behavior).GetVariable(names[index]) : (object) GlobalVariables.Instance.GetVariable(synchronizer.targetName)).GetType().GetProperty("Value").PropertyType.FullName;
            this.sharedVariableValueType = (System.Type) null;
          }
        }
        else
          synchronizer.targetName = (string) null;
      }
      if (string.IsNullOrEmpty(synchronizer.targetName))
        GUI.enabled = false;
      return GUI.enabled;
    }

    private static Behavior GetBehaviorWithGroup(Behavior[] behaviors, int group)
    {
      if (behaviors == null || behaviors.Length == 0)
        return (Behavior) null;
      if (behaviors.Length == 1)
        return behaviors[0];
      for (int index = 0; index < behaviors.Length; ++index)
      {
        if (behaviors[index].Group == group)
          return behaviors[index];
      }
      return behaviors[0];
    }

    private void DrawPropertySynchronizer(
      VariableSynchronizerInspector.Synchronizer synchronizer,
      System.Type valueType)
    {
      VariableSynchronizerInspector.DrawComponentSelector(synchronizer, typeof (Component), VariableSynchronizerInspector.ComponentListType.Popup);
      int num = 0;
      List<string> stringList = new List<string>();
      stringList.Add("None");
      if ((UnityEngine.Object) synchronizer.component != (UnityEngine.Object) null)
      {
        PropertyInfo[] properties = ((object) synchronizer.component).GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        for (int index = 0; index < properties.Length; ++index)
        {
          if (properties[index].PropertyType.Equals(valueType) && !properties[index].IsSpecialName)
          {
            if (properties[index].Name.Equals(synchronizer.targetName))
              num = stringList.Count;
            stringList.Add(properties[index].Name);
          }
        }
      }
      EditorGUI.BeginChangeCheck();
      int index1 = EditorGUILayout.Popup("Property", num, stringList.ToArray(), Array.Empty<GUILayoutOption>());
      if (!EditorGUI.EndChangeCheck())
        return;
      if (index1 != 0)
        synchronizer.targetName = stringList[index1];
      else
        synchronizer.targetName = string.Empty;
    }

    private void DrawAnimatorSynchronizer(
      VariableSynchronizerInspector.Synchronizer synchronizer)
    {
      VariableSynchronizerInspector.DrawComponentSelector(synchronizer, typeof (Animator), VariableSynchronizerInspector.ComponentListType.Instant);
      synchronizer.targetName = EditorGUILayout.TextField("Parameter Name", synchronizer.targetName, Array.Empty<GUILayoutOption>());
    }

    private void DrawPlayMakerSynchronizer(
      VariableSynchronizerInspector.Synchronizer synchronizer,
      System.Type valueType)
    {
      if (this.playMakerSynchronizationType == (System.Type) null)
      {
        this.playMakerSynchronizationType = System.Type.GetType("BehaviorDesigner.Editor.VariableSynchronizerInspector_PlayMaker, Assembly-CSharp-Editor");
        if (this.playMakerSynchronizationType == (System.Type) null)
        {
          EditorGUILayout.LabelField("Unable to find PlayMaker inspector task.", Array.Empty<GUILayoutOption>());
          return;
        }
      }
      if (this.thirdPartySynchronizer == null)
      {
        MethodInfo method = this.playMakerSynchronizationType.GetMethod(nameof (DrawPlayMakerSynchronizer));
        if (method != (MethodInfo) null)
          this.thirdPartySynchronizer = (Action<VariableSynchronizerInspector.Synchronizer, System.Type>) Delegate.CreateDelegate(typeof (Action<VariableSynchronizerInspector.Synchronizer, System.Type>), method);
      }
      this.thirdPartySynchronizer(synchronizer, valueType);
    }

    private void DrawuFrameSynchronizer(
      VariableSynchronizerInspector.Synchronizer synchronizer,
      System.Type valueType)
    {
      if (this.uFrameSynchronizationType == (System.Type) null)
      {
        this.uFrameSynchronizationType = System.Type.GetType("BehaviorDesigner.Editor.VariableSynchronizerInspector_uFrame, Assembly-CSharp-Editor");
        if (this.uFrameSynchronizationType == (System.Type) null)
        {
          EditorGUILayout.LabelField("Unable to find uFrame inspector task.", Array.Empty<GUILayoutOption>());
          return;
        }
      }
      if (this.thirdPartySynchronizer == null)
      {
        MethodInfo method = this.uFrameSynchronizationType.GetMethod("DrawSynchronizer");
        if (method != (MethodInfo) null)
          this.thirdPartySynchronizer = (Action<VariableSynchronizerInspector.Synchronizer, System.Type>) Delegate.CreateDelegate(typeof (Action<VariableSynchronizerInspector.Synchronizer, System.Type>), method);
      }
      this.thirdPartySynchronizer(synchronizer, valueType);
    }

    private void DrawSynchronizedVariables(VariableSynchronizer variableSynchronizer)
    {
      GUI.enabled = true;
      if (variableSynchronizer.SynchronizedVariables == null || variableSynchronizer.SynchronizedVariables.Count == 0)
        return;
      Rect lastRect = GUILayoutUtility.GetLastRect();
      lastRect.x = -5f;
      lastRect.y += lastRect.height + 1f;
      lastRect.height = 2f;
      lastRect.width += 20f;
      GUI.DrawTexture(lastRect, (Texture) BehaviorDesignerUtility.LoadTexture("ContentSeparator.png", obj: ((UnityEngine.Object) this)));
      GUILayout.Space(6f);
      for (int index = 0; index < variableSynchronizer.SynchronizedVariables.Count; ++index)
      {
        VariableSynchronizer.SynchronizedVariable synchronizedVariable = variableSynchronizer.SynchronizedVariables[index];
        if (synchronizedVariable.global)
        {
          if (GlobalVariables.Instance.GetVariable(synchronizedVariable.variableName) == null)
          {
            variableSynchronizer.SynchronizedVariables.RemoveAt(index);
            break;
          }
        }
        else if (synchronizedVariable.behavior.GetVariable(synchronizedVariable.variableName) == null)
        {
          variableSynchronizer.SynchronizedVariables.RemoveAt(index);
          break;
        }
        EditorGUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        EditorGUILayout.LabelField(synchronizedVariable.variableName, new GUILayoutOption[1]
        {
          GUILayout.MaxWidth(120f)
        });
        if (GUILayout.Button((Texture) BehaviorDesignerUtility.LoadTexture(!synchronizedVariable.setVariable ? "RightArrowButton.png" : "LeftArrowButton.png", obj: ((UnityEngine.Object) this)), BehaviorDesignerUtility.ButtonGUIStyle, new GUILayoutOption[1]
        {
          GUILayout.Width(22f)
        }) && !Application.isPlaying)
          synchronizedVariable.setVariable = !synchronizedVariable.setVariable;
        EditorGUILayout.LabelField(string.Format("{0} ({1})", (object) synchronizedVariable.targetName, (object) synchronizedVariable.synchronizationType.ToString()), new GUILayoutOption[1]
        {
          GUILayout.MinWidth(120f)
        });
        GUILayout.FlexibleSpace();
        if (GUILayout.Button((Texture) BehaviorDesignerUtility.LoadTexture("DeleteButton.png", obj: ((UnityEngine.Object) this)), BehaviorDesignerUtility.ButtonGUIStyle, new GUILayoutOption[1]
        {
          GUILayout.Width(22f)
        }))
        {
          variableSynchronizer.SynchronizedVariables.RemoveAt(index);
          EditorGUILayout.EndHorizontal();
          break;
        }
        GUILayout.Space(2f);
        EditorGUILayout.EndHorizontal();
        GUILayout.Space(2f);
      }
      GUILayout.Space(4f);
    }

    public enum ComponentListType
    {
      Instant,
      Popup,
      BehaviorDesignerGroup,
      None,
    }

    [Serializable]
    public class Synchronizer
    {
      public GameObject gameObject;
      public Component component;
      public string targetName;
      public bool global;
      public int componentGroup;
      public string componentName;
    }
  }
}
