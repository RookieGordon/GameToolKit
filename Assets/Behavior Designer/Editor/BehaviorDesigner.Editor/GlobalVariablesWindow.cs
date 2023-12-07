// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.GlobalVariablesWindow
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
    public class GlobalVariablesWindow : EditorWindow
    {
        private string mVariableName = string.Empty;
        private int mVariableTypeIndex;
        private Vector2 mScrollPosition = Vector2.zero;
        private bool mFocusNameField;
        [SerializeField] private float mVariableStartPosition = -1f;
        [SerializeField] private List<float> mVariablePosition;
        [SerializeField] private int mSelectedVariableIndex = -1;
        [SerializeField] private string mSelectedVariableName;
        [SerializeField] private int mSelectedVariableTypeIndex;
        private GlobalVariables mVariableSource;
        public static GlobalVariablesWindow instance;

        [MenuItem("Tools/Behavior Designer/Global Variables", false, 1)]
        public static void ShowWindow()
        {
            GlobalVariablesWindow window = EditorWindow.GetWindow<GlobalVariablesWindow>(false, "Global Variables");
            window.minSize = new Vector2(300f, 410f);
            window.maxSize = new Vector2(300f, float.MaxValue);
            window.wantsMouseMove = true;
        }

        public void OnFocus()
        {
            GlobalVariablesWindow.instance = this;
            this.mVariableSource = GlobalVariables.Instance;
            if ((Object)this.mVariableSource != (Object)null)
                this.mVariableSource.CheckForSerialization(!Application.isPlaying);
            FieldInspector.Init();
        }

        public void OnGUI()
        {
            if ((Object)this.mVariableSource == (Object)null)
                this.mVariableSource = GlobalVariables.Instance;
            if (VariableInspector.DrawVariables((IVariableSource)this.mVariableSource,
                    (BehaviorSource)null,
                    ref this.mVariableName,
                    ref this.mFocusNameField,
                    ref this.mVariableTypeIndex,
                    ref this.mScrollPosition,
                    ref this.mVariablePosition,
                    ref this.mVariableStartPosition,
                    ref this.mSelectedVariableIndex,
                    ref this.mSelectedVariableName,
                    ref this.mSelectedVariableTypeIndex))
                this.SerializeVariables();
            if (Event.current.type != null || !VariableInspector.LeftMouseDown((IVariableSource)this.mVariableSource,
                    (BehaviorSource)null,
                    Event.current.mousePosition,
                    this.mVariablePosition,
                    this.mVariableStartPosition,
                    this.mScrollPosition,
                    ref this.mSelectedVariableIndex,
                    ref this.mSelectedVariableName,
                    ref this.mSelectedVariableTypeIndex))
                return;
            Event.current.Use();
            this.Repaint();
        }

        private void SerializeVariables()
        {
            if ((Object)this.mVariableSource == (Object)null)
                this.mVariableSource = GlobalVariables.Instance;
            if (BehaviorDesignerPreferences.GetBool(BDPreferences.BinarySerialization))
                BinarySerialization.Save(this.mVariableSource);
            else
                JSONSerialization.Save(this.mVariableSource);
        }
    }
}