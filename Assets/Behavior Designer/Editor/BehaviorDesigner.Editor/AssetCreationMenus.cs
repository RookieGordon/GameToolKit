// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.AssetCreationMenus
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using UnityEditor;

namespace BehaviorDesigner.Editor
{
  public class AssetCreationMenus
  {
    [MenuItem("Assets/Create/Behavior Designer/C# Action Task")]
    public static void CreateCSharpActionTask() => AssetCreator.ShowWindow(AssetCreator.AssetClassType.Action);

    [MenuItem("Assets/Create/Behavior Designer/C# Conditional Task")]
    public static void CreateCSharpConditionalTask() => AssetCreator.ShowWindow(AssetCreator.AssetClassType.Conditional);

    [MenuItem("Assets/Create/Behavior Designer/Shared Variable")]
    public static void CreateSharedVariable() => AssetCreator.ShowWindow(AssetCreator.AssetClassType.SharedVariable);

    [MenuItem("Assets/Create/Behavior Designer/External Behavior Tree")]
    public static void CreateExternalBehaviorTree() => AssetCreator.CreateAsset(TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.ExternalBehaviorTree"), "NewExternalBehavior");
  }
}
