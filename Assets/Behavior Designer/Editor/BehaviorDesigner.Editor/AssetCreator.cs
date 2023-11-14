// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.AssetCreator
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
  public class AssetCreator : EditorWindow
  {
    private AssetCreator.AssetClassType m_ClassType;
    private string m_AssetName;

    private AssetCreator.AssetClassType ClassType
    {
      set
      {
        this.m_ClassType = value;
        switch (this.m_ClassType)
        {
          case AssetCreator.AssetClassType.Action:
            this.m_AssetName = "NewAction";
            break;
          case AssetCreator.AssetClassType.Conditional:
            this.m_AssetName = "NewConditional";
            break;
          case AssetCreator.AssetClassType.SharedVariable:
            this.m_AssetName = "SharedNewVariable";
            break;
        }
      }
    }

    public static void ShowWindow(AssetCreator.AssetClassType classType)
    {
      AssetCreator window = EditorWindow.GetWindow<AssetCreator>(true, "Asset Name");
      AssetCreator assetCreator = window;
      Vector2 vector2_1 = new Vector2(300f, 55f);
      window.maxSize = vector2_1;
      Vector2 vector2_2 = vector2_1;
      assetCreator.minSize = vector2_2;
      window.ClassType = classType;
    }

    private void OnGUI()
    {
      this.m_AssetName = EditorGUILayout.TextField("Name", this.m_AssetName, Array.Empty<GUILayoutOption>());
      EditorGUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
      if (GUILayout.Button("OK", Array.Empty<GUILayoutOption>()))
      {
        AssetCreator.CreateScript(this.m_AssetName, this.m_ClassType);
        this.Close();
      }
      if (GUILayout.Button("Cancel", Array.Empty<GUILayoutOption>()))
        this.Close();
      EditorGUILayout.EndHorizontal();
    }

    public static void CreateAsset(System.Type type, string name)
    {
      ScriptableObject instance = ScriptableObject.CreateInstance(type);
      string path = AssetDatabase.GetAssetPath(Selection.activeObject);
      if (path == string.Empty)
        path = "Assets";
      else if (Path.GetExtension(path) != string.Empty)
        path = path.Replace(Path.GetFileName(AssetDatabase.GetAssetPath(Selection.activeObject)), string.Empty);
      string uniqueAssetPath = AssetDatabase.GenerateUniqueAssetPath(path + "/" + name + ".asset");
      AssetDatabase.CreateAsset((UnityEngine.Object) instance, uniqueAssetPath);
      AssetDatabase.SaveAssets();
    }

    private static void CreateScript(string name, AssetCreator.AssetClassType classType)
    {
      string path = AssetDatabase.GetAssetPath(Selection.activeObject);
      if (path == string.Empty)
        path = "Assets";
      else if (Path.GetExtension(path) != string.Empty)
        path = path.Replace(Path.GetFileName(AssetDatabase.GetAssetPath(Selection.activeObject)), string.Empty);
      string uniqueAssetPath = AssetDatabase.GenerateUniqueAssetPath(path + "/" + name + ".cs");
      StreamWriter streamWriter = new StreamWriter(uniqueAssetPath, false);
      string withoutExtension = Path.GetFileNameWithoutExtension(uniqueAssetPath);
      string str = string.Empty;
      switch (classType)
      {
        case AssetCreator.AssetClassType.Action:
          str = AssetCreator.ActionTaskContents(withoutExtension);
          break;
        case AssetCreator.AssetClassType.Conditional:
          str = AssetCreator.ConditionalTaskContents(withoutExtension);
          break;
        case AssetCreator.AssetClassType.SharedVariable:
          str = AssetCreator.SharedVariableContents(withoutExtension);
          break;
      }
      streamWriter.Write(str);
      streamWriter.Close();
      AssetDatabase.Refresh();
    }

    private static string ActionTaskContents(string name) => "using UnityEngine;\nusing BehaviorDesigner.Runtime;\nusing BehaviorDesigner.Runtime.Tasks;\n\npublic class " + name + " : Action\n{\n\tpublic override void OnStart()\n\t{\n\t\t\n\t}\n\n\tpublic override TaskStatus OnUpdate()\n\t{\n\t\treturn TaskStatus.Success;\n\t}\n}";

    private static string ConditionalTaskContents(string name) => "using UnityEngine;\nusing BehaviorDesigner.Runtime;\nusing BehaviorDesigner.Runtime.Tasks;\n\npublic class " + name + " : Conditional\n{\n\tpublic override TaskStatus OnUpdate()\n\t{\n\t\treturn TaskStatus.Success;\n\t}\n}";

    private static string SharedVariableContents(string name)
    {
      string str = name.Remove(0, 6);
      return "using UnityEngine;\nusing BehaviorDesigner.Runtime;\n\n[System.Serializable]\npublic class " + str + "\n{\n\n}\n\n[System.Serializable]\npublic class " + name + " : SharedVariable<" + str + ">\n{\n\tpublic override string ToString() { return mValue == null ? \"null\" : mValue.ToString(); }\n\tpublic static implicit operator " + name + "(" + str + " value) { return new " + name + " { mValue = value }; }\n}";
    }

    public enum AssetClassType
    {
      Action,
      Conditional,
      SharedVariable,
    }
  }
}
