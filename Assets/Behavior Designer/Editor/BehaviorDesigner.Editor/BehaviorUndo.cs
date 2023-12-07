// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.BehaviorUndo
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using UnityEditor;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
    public class BehaviorUndo
    {
        public static void RegisterUndo(string undoName, UnityEngine.Object undoObject)
        {
            if (!BehaviorDesignerPreferences.GetBool(BDPreferences.UndoRedo))
                return;
            Undo.RecordObject(undoObject, undoName);
        }

        public static Component AddComponent(GameObject undoObject, System.Type type) => Undo.AddComponent(undoObject, type);

        public static void DestroyObject(UnityEngine.Object undoObject, bool registerScene) => Undo.DestroyObjectImmediate(undoObject);
    }
}