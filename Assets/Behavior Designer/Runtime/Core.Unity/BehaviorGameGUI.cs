// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.BehaviorGameGUI
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using BehaviorDesigner.Runtime.Tasks;
using System.Collections.Generic;
using UnityEngine;

namespace BehaviorDesigner.Runtime
{
    [AddComponentMenu("Behavior Designer/Behavior Game GUI")]
    public class BehaviorGameGUI : MonoBehaviour
    {
        private BehaviorManager behaviorManager;
        private Camera mainCamera;

        public void Start() => this.mainCamera = Camera.main;

        public void OnGUI()
        {
            if ((Object)this.behaviorManager == (Object)null)
                this.behaviorManager = BehaviorManager.instance;
            if ((Object)this.behaviorManager == (Object)null || (Object)this.mainCamera == (Object)null)
                return;
            List<BehaviorManager.BehaviorTree> behaviorTrees = this.behaviorManager.BehaviorTrees;
            for (int index1 = 0; index1 < behaviorTrees.Count; ++index1)
            {
                BehaviorManager.BehaviorTree behaviorTree = behaviorTrees[index1];
                string str = string.Empty;
                for (int index2 = 0; index2 < behaviorTree.activeStack.Count; ++index2)
                {
                    Stack<int> active = behaviorTree.activeStack[index2];
                    if (active.Count != 0 && behaviorTree.taskList[active.Peek()] is Action)
                        str = str + behaviorTree.taskList[behaviorTree.activeStack[index2].Peek()].FriendlyName + (index2 >= behaviorTree.activeStack.Count - 1 ? string.Empty : "\n");
                }

                Vector2 guiPoint = GUIUtility.ScreenToGUIPoint((Vector2)Camera.main.WorldToScreenPoint(behaviorTree.behavior.transform.position));
                GUIContent guiContent = new GUIContent(str);
                Vector2 vector2 = GUI.skin.label.CalcSize(guiContent);
                vector2.x += 14f;
                vector2.y += 5f;
                GUI.Box(new Rect(guiPoint.x - vector2.x / 2f, (float)((double)Screen.height - (double)guiPoint.y + (double)vector2.y / 2.0), vector2.x, vector2.y), guiContent);
            }
        }
    }
}