// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.BehaviorDesignerPreferences
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
    public class BehaviorDesignerPreferences : UnityEditor.Editor
    {
        private static string[] prefString;

        private static string[] serializationString = new string[2]
        {
            "Binary",
            "JSON"
        };

        private static Dictionary<BDPreferences, object> prefToValue = new Dictionary<BDPreferences, object>();

        private static string[] PrefString
        {
            get
            {
                if (BehaviorDesignerPreferences.prefString == null)
                    BehaviorDesignerPreferences.InitPrefString();
                return BehaviorDesignerPreferences.prefString;
            }
        }

        public static void InitPrefernces()
        {
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[0]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.ShowWelcomeScreen, true);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[1]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.ShowSceneIcon, true);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[2]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.ShowHierarchyIcon, true);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[3]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.OpenInspectorOnTaskSelection, false);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[3]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.OpenInspectorOnTaskSelection, false);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[5]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.FadeNodes, true);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[6]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.EditablePrefabInstances, false);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[7]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.PropertiesPanelOnLeft, true);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[8]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.MouseWhellScrolls, false);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[9]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.PasteAtCursor, false);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[10]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.FoldoutFields, true);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[11]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.CompactMode, false);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[12]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.SnapToGrid, true);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[13]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.ShowTaskDescription, true);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[14]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.BinarySerialization, false);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[15]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.UndoRedo, true);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[16]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.ErrorChecking, true);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[17]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.SelectOnBreakpoint, false);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[18]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.UpdateCheck, true);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[19]))
                BehaviorDesignerPreferences.SetBool(BDPreferences.AddGameGUIComponent, false);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[20]))
                BehaviorDesignerPreferences.SetInt(BDPreferences.GizmosViewMode, 2);
            if (!EditorPrefs.HasKey(BehaviorDesignerPreferences.PrefString[21]))
                BehaviorDesignerPreferences.SetInt(BDPreferences.QuickSearchKeyCode, 32);
            if (!BehaviorDesignerPreferences.GetBool(BDPreferences.EditablePrefabInstances) || !BehaviorDesignerPreferences.GetBool(BDPreferences.BinarySerialization))
                return;
            BehaviorDesignerPreferences.SetBool(BDPreferences.BinarySerialization, false);
        }

        private static void InitPrefString()
        {
            BehaviorDesignerPreferences.prefString = new string[23];
            for (int index = 0; index < BehaviorDesignerPreferences.prefString.Length; ++index)
                BehaviorDesignerPreferences.prefString[index] = string.Format("BehaviorDesigner{0}", (object)(BDPreferences)index);
        }

        public static void DrawPreferencesPane(PreferenceChangeHandler callback)
        {
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.ShowWelcomeScreen, "Show welcome screen", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.ShowSceneIcon, "Show Behavior Designer icon in the scene", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.ShowHierarchyIcon, "Show Behavior Designer icon in the hierarchy", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.OpenInspectorOnTaskSelection, "Open inspector on single task selection", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.OpenInspectorOnTaskDoubleClick, "Open inspector on task double click", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.FadeNodes, "Fade tasks after they are done running", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.EditablePrefabInstances, "Allow edit of prefab instances", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.PropertiesPanelOnLeft, "Position properties panel on the left", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.MouseWhellScrolls, "Mouse wheel scrolls graph view", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.PasteAtCursor, "Paste copied task at the cursor location", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.FoldoutFields, "Grouped fields start visible", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.CompactMode, "Compact mode", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.SnapToGrid, "Snap to grid", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.ShowTaskDescription, "Show selected task description", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.UndoRedo, "Record undo/redo", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.ErrorChecking, "Realtime error checking", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.SelectOnBreakpoint, "Select GameObject if a breakpoint is hit", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.UpdateCheck, "Check for updates", callback);
            BehaviorDesignerPreferences.DrawBoolPref(BDPreferences.AddGameGUIComponent, "Add Game GUI Component", callback);
            bool flag = BehaviorDesignerPreferences.GetBool(BDPreferences.BinarySerialization);
            if (EditorGUILayout.Popup("Serialization", !flag ? 1 : 0, BehaviorDesignerPreferences.serializationString, Array.Empty<GUILayoutOption>()) != (!flag ? 1 : 0))
            {
                BehaviorDesignerPreferences.SetBool(BDPreferences.BinarySerialization, !flag);
                callback(BDPreferences.BinarySerialization, (object)!flag);
            }

            int num1 = BehaviorDesignerPreferences.GetInt(BDPreferences.GizmosViewMode);
            int num2 = (int)(Behavior.GizmoViewMode)EditorGUILayout.EnumPopup("Gizmos View Mode", (Enum)(object)(Behavior.GizmoViewMode)num1, Array.Empty<GUILayoutOption>());
            if (num2 != num1)
            {
                BehaviorDesignerPreferences.SetInt(BDPreferences.GizmosViewMode, (int)(object)(Behavior.GizmoViewMode)num2);
                callback(BDPreferences.GizmosViewMode, (object)num2);
            }

            int num3 = BehaviorDesignerPreferences.GetInt(BDPreferences.QuickSearchKeyCode);
            KeyCode num4 = (KeyCode)EditorGUILayout.EnumPopup("Quick Search Key Code", (KeyCode)num3, Array.Empty<GUILayoutOption>());
            if (num4 != (KeyCode)num3)
            {
                BehaviorDesignerPreferences.SetInt(BDPreferences.QuickSearchKeyCode, (int)num4);
                callback(BDPreferences.QuickSearchKeyCode, (object)(int)num4);
            }

            float num5 = BehaviorDesignerPreferences.GetFloat(BDPreferences.ZoomSpeedMultiplier);
            float num6 = EditorGUILayout.Slider("Zoom Speed Multiplier", num5, 0.1f, 4f, Array.Empty<GUILayoutOption>());
            if ((double)num5 != (double)num6)
            {
                BehaviorDesignerPreferences.SetFloat(BDPreferences.ZoomSpeedMultiplier, num6);
                callback(BDPreferences.ZoomSpeedMultiplier, (object)num6);
            }

            if (!GUILayout.Button("Restore to Defaults", EditorStyles.miniButtonMid, Array.Empty<GUILayoutOption>()))
                return;
            BehaviorDesignerPreferences.ResetPrefs();
        }

        private static void DrawBoolPref(
            BDPreferences pref,
            string text,
            PreferenceChangeHandler callback)
        {
            bool flag1 = BehaviorDesignerPreferences.GetBool(pref);
            bool flag2 = GUILayout.Toggle(flag1, text, Array.Empty<GUILayoutOption>());
            if (flag2 == flag1)
                return;
            BehaviorDesignerPreferences.SetBool(pref, flag2);
            callback(pref, (object)flag2);
        }

        private static void ResetPrefs()
        {
            BehaviorDesignerPreferences.SetBool(BDPreferences.ShowWelcomeScreen, true);
            BehaviorDesignerPreferences.SetBool(BDPreferences.ShowSceneIcon, true);
            BehaviorDesignerPreferences.SetBool(BDPreferences.ShowHierarchyIcon, true);
            BehaviorDesignerPreferences.SetBool(BDPreferences.OpenInspectorOnTaskSelection, false);
            BehaviorDesignerPreferences.SetBool(BDPreferences.OpenInspectorOnTaskDoubleClick, false);
            BehaviorDesignerPreferences.SetBool(BDPreferences.FadeNodes, true);
            BehaviorDesignerPreferences.SetBool(BDPreferences.EditablePrefabInstances, false);
            BehaviorDesignerPreferences.SetBool(BDPreferences.PropertiesPanelOnLeft, true);
            BehaviorDesignerPreferences.SetBool(BDPreferences.MouseWhellScrolls, false);
            BehaviorDesignerPreferences.SetBool(BDPreferences.PasteAtCursor, false);
            BehaviorDesignerPreferences.SetBool(BDPreferences.FoldoutFields, true);
            BehaviorDesignerPreferences.SetBool(BDPreferences.CompactMode, false);
            BehaviorDesignerPreferences.SetBool(BDPreferences.SnapToGrid, true);
            BehaviorDesignerPreferences.SetBool(BDPreferences.ShowTaskDescription, true);
            BehaviorDesignerPreferences.SetBool(BDPreferences.BinarySerialization, false);
            BehaviorDesignerPreferences.SetBool(BDPreferences.UndoRedo, true);
            BehaviorDesignerPreferences.SetBool(BDPreferences.ErrorChecking, true);
            BehaviorDesignerPreferences.SetBool(BDPreferences.SelectOnBreakpoint, false);
            BehaviorDesignerPreferences.SetBool(BDPreferences.UpdateCheck, true);
            BehaviorDesignerPreferences.SetBool(BDPreferences.AddGameGUIComponent, false);
            BehaviorDesignerPreferences.SetInt(BDPreferences.GizmosViewMode, 2);
            BehaviorDesignerPreferences.SetInt(BDPreferences.QuickSearchKeyCode, 32);
            BehaviorDesignerPreferences.SetInt(BDPreferences.ZoomSpeedMultiplier, 1);
        }

        public static void SetBool(BDPreferences pref, bool value)
        {
            EditorPrefs.SetBool(BehaviorDesignerPreferences.PrefString[(int)pref], value);
            BehaviorDesignerPreferences.prefToValue[pref] = (object)value;
        }

        public static bool GetBool(BDPreferences pref)
        {
            object obj1;
            if (BehaviorDesignerPreferences.prefToValue.TryGetValue(pref, out obj1))
                return (bool)obj1;
            object obj2 = (object)EditorPrefs.GetBool(BehaviorDesignerPreferences.PrefString[(int)pref]);
            BehaviorDesignerPreferences.prefToValue.Add(pref, obj2);
            return (bool)obj2;
        }

        public static void SetInt(BDPreferences pref, int value)
        {
            EditorPrefs.SetInt(BehaviorDesignerPreferences.PrefString[(int)pref], value);
            BehaviorDesignerPreferences.prefToValue[pref] = (object)value;
        }

        public static int GetInt(BDPreferences pref)
        {
            object obj1;
            if (BehaviorDesignerPreferences.prefToValue.TryGetValue(pref, out obj1))
                return (int)obj1;
            object obj2 = (object)EditorPrefs.GetInt(BehaviorDesignerPreferences.PrefString[(int)pref]);
            BehaviorDesignerPreferences.prefToValue.Add(pref, obj2);
            return (int)obj2;
        }

        public static void SetFloat(BDPreferences pref, float value)
        {
            EditorPrefs.SetFloat(BehaviorDesignerPreferences.PrefString[(int)pref], value);
            BehaviorDesignerPreferences.prefToValue[pref] = (object)value;
        }

        public static float GetFloat(BDPreferences pref)
        {
            object obj1;
            if (BehaviorDesignerPreferences.prefToValue.TryGetValue(pref, out obj1))
                return (float)obj1;
            object obj2 = (object)EditorPrefs.GetFloat(BehaviorDesignerPreferences.PrefString[(int)pref], 1f);
            BehaviorDesignerPreferences.prefToValue.Add(pref, obj2);
            return (float)obj2;
        }
    }
}