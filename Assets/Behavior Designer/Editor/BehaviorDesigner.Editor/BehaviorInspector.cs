// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.BehaviorInspector
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
    [CustomEditor(typeof(Behavior))]
    public class BehaviorInspector : UnityEditor.Editor
    {
        private bool mShowOptions = true;
        private bool mShowVariables;
        private static List<float> variablePosition;
        private static int selectedVariableIndex = -1;
        private static string selectedVariableName;
        private static int selectedVariableTypeIndex;

        private void OnEnable()
        {
            Behavior target = this.target as Behavior;
            if ((UnityEngine.Object)target == (UnityEngine.Object)null)
                return;
            GizmoManager.UpdateGizmo(target);
            if (Application.isPlaying && EditorApplication.isPlayingOrWillChangePlaymode)
                BehaviorManager.IsPlaying = true;
            target.CheckForSerialization((UnityEngine.Object)BehaviorDesignerWindow.instance == (UnityEngine.Object)null && !Application.isPlaying, Application.isPlaying);
            if (Application.isPlaying || !((UnityEngine.Object)target.ExternalBehavior != (UnityEngine.Object)null) || !((UnityEngine.Object)BehaviorDesignerWindow.instance == (UnityEngine.Object)null))
                return;
            target.ExternalBehavior.BehaviorSource.CheckForSerialization(true, (BehaviorSource)null, false);
            if (!VariableInspector.SyncVariables(target.GetBehaviorSource(), target.ExternalBehavior.BehaviorSource.GetAllVariables()))
                return;
            if (BehaviorDesignerPreferences.GetBool(BDPreferences.BinarySerialization))
                BinarySerialization.Save(target.GetBehaviorSource());
            else
                JSONSerialization.Save(target.GetBehaviorSource());
        }

        public override void OnInspectorGUI()
        {
            Behavior target = this.target as Behavior;
            if ((UnityEngine.Object)target == (UnityEngine.Object)null)
                return;
            bool externalModification = false;
            if (!BehaviorInspector.DrawInspectorGUI(target, this.serializedObject, true, ref externalModification, ref this.mShowOptions, ref this.mShowVariables))
                return;
            BehaviorDesignerUtility.SetObjectDirty((UnityEngine.Object)target);
            if (!externalModification || !((UnityEngine.Object)BehaviorDesignerWindow.instance != (UnityEngine.Object)null) || target.GetBehaviorSource().BehaviorID != BehaviorDesignerWindow.instance.ActiveBehaviorID)
                return;
            BehaviorDesignerWindow.instance.LoadBehavior(target.GetBehaviorSource(), false, false);
        }

        public static bool DrawInspectorGUI(
            Behavior behavior,
            SerializedObject serializedObject,
            bool fromInspector,
            ref bool externalModification,
            ref bool showOptions,
            ref bool showVariables)
        {
            EditorGUI.BeginChangeCheck();
            GUILayout.Space(3f);
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            EditorGUILayout.LabelField("Behavior Name",
                new GUILayoutOption[1]
                {
                    GUILayout.Width(120f)
                });
            behavior.GetBehaviorSource().behaviorName = EditorGUILayout.TextField(behavior.GetBehaviorSource().behaviorName, Array.Empty<GUILayoutOption>());
            if (fromInspector && GUILayout.Button("Open", Array.Empty<GUILayoutOption>()))
            {
                BehaviorDesignerWindow.ShowWindow();
                BehaviorDesignerWindow.instance.LoadBehavior(behavior.GetBehaviorSource(), false, true);
            }

            GUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Behavior Description", Array.Empty<GUILayoutOption>());
            behavior.GetBehaviorSource().behaviorDescription = EditorGUILayout.TextArea(behavior.GetBehaviorSource().behaviorDescription,
                BehaviorDesignerUtility.TaskInspectorCommentGUIStyle,
                new GUILayoutOption[1]
                {
                    GUILayout.Height(48f)
                });
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            GUI.enabled = BehaviorDesignerPreferences.GetBool(BDPreferences.EditablePrefabInstances) || PrefabUtility.GetPrefabAssetType((UnityEngine.Object)behavior) != PrefabAssetType.Regular && PrefabUtility.GetPrefabAssetType((UnityEngine.Object)behavior) != PrefabAssetType.Variant;
            SerializedProperty property = serializedObject.FindProperty("externalBehavior");
            ExternalBehavior objectReferenceValue = property.objectReferenceValue as ExternalBehavior;
            EditorGUILayout.PropertyField(property, true, Array.Empty<GUILayoutOption>());
            if (EditorGUI.EndChangeCheck())
                serializedObject.ApplyModifiedProperties();
            if (!object.ReferenceEquals((object)behavior.ExternalBehavior, (object)null) && !((object)behavior.ExternalBehavior).Equals((object)objectReferenceValue) ||
                !object.ReferenceEquals((object)objectReferenceValue, (object)null) && !((object)objectReferenceValue).Equals((object)behavior.ExternalBehavior))
            {
                if (!object.ReferenceEquals((object)behavior.ExternalBehavior, (object)null))
                {
                    behavior.ExternalBehavior.BehaviorSource.Owner = (IBehavior)behavior.ExternalBehavior;
                    behavior.ExternalBehavior.BehaviorSource.CheckForSerialization(true, behavior.GetBehaviorSource(), false);
                }
                else
                {
                    behavior.GetBehaviorSource().EntryTask = (Task)null;
                    behavior.GetBehaviorSource().RootTask = (Task)null;
                    behavior.GetBehaviorSource().DetachedTasks = (List<Task>)null;
                    behavior.GetBehaviorSource().Variables = (List<SharedVariable>)null;
                    behavior.GetBehaviorSource().CheckForSerialization(true, (BehaviorSource)null, false);
                    behavior.GetBehaviorSource().Variables = (List<SharedVariable>)null;
                    if (BehaviorDesignerPreferences.GetBool(BDPreferences.BinarySerialization))
                        BinarySerialization.Save(behavior.GetBehaviorSource());
                    else
                        JSONSerialization.Save(behavior.GetBehaviorSource());
                }

                externalModification = true;
            }

            GUI.enabled = true;
            EditorGUILayout.PropertyField(serializedObject.FindProperty("group"), true, Array.Empty<GUILayoutOption>());
            if (fromInspector)
            {
                string str = "BehaviorDesigner.VariablesFoldout." + (object)((object)behavior).GetHashCode();
                if (showVariables = EditorGUILayout.Foldout(EditorPrefs.GetBool(str, true), "Variables"))
                {
                    ++EditorGUI.indentLevel;
                    bool flag = false;
                    BehaviorSource behaviorSource1 = behavior.GetBehaviorSource();
                    List<SharedVariable> allVariables = behaviorSource1.GetAllVariables();
                    if (allVariables != null && allVariables.Count > 0)
                    {
                        if (VariableInspector.DrawAllVariables(false,
                                (IVariableSource)behaviorSource1,
                                ref allVariables,
                                false,
                                ref BehaviorInspector.variablePosition,
                                ref BehaviorInspector.selectedVariableIndex,
                                ref BehaviorInspector.selectedVariableName,
                                ref BehaviorInspector.selectedVariableTypeIndex,
                                false,
                                true))
                        {
                            if (!EditorApplication.isPlayingOrWillChangePlaymode && (UnityEngine.Object)behavior.ExternalBehavior != (UnityEngine.Object)null)
                            {
                                BehaviorSource behaviorSource2 = behavior.ExternalBehavior.GetBehaviorSource();
                                behaviorSource2.CheckForSerialization(true, (BehaviorSource)null, false);
                                if (VariableInspector.SyncVariables(behaviorSource2, allVariables))
                                {
                                    if (BehaviorDesignerPreferences.GetBool(BDPreferences.BinarySerialization))
                                        BinarySerialization.Save(behaviorSource2);
                                    else
                                        JSONSerialization.Save(behaviorSource2);
                                }
                            }

                            flag = true;
                        }
                    }
                    else
                        EditorGUILayout.LabelField("There are no variables to display", Array.Empty<GUILayoutOption>());

                    if (flag)
                    {
                        if (BehaviorDesignerPreferences.GetBool(BDPreferences.BinarySerialization))
                            BinarySerialization.Save(behaviorSource1);
                        else
                            JSONSerialization.Save(behaviorSource1);
                    }

                    --EditorGUI.indentLevel;
                }

                EditorPrefs.SetBool(str, showVariables);
            }

            string str1 = "BehaviorDesigner.OptionsFoldout." + (object)((object)behavior).GetHashCode();
            if (!fromInspector || (showOptions = EditorGUILayout.Foldout(EditorPrefs.GetBool(str1, true), "Options")))
            {
                if (fromInspector)
                    ++EditorGUI.indentLevel;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("startWhenEnabled"), true, Array.Empty<GUILayoutOption>());
                EditorGUILayout.PropertyField(serializedObject.FindProperty("asynchronousLoad"), true, Array.Empty<GUILayoutOption>());
                EditorGUILayout.PropertyField(serializedObject.FindProperty("pauseWhenDisabled"), true, Array.Empty<GUILayoutOption>());
                EditorGUILayout.PropertyField(serializedObject.FindProperty("restartWhenComplete"), true, Array.Empty<GUILayoutOption>());
                EditorGUILayout.PropertyField(serializedObject.FindProperty("resetValuesOnRestart"), true, Array.Empty<GUILayoutOption>());
                EditorGUILayout.PropertyField(serializedObject.FindProperty("logTaskChanges"), true, Array.Empty<GUILayoutOption>());
                if (fromInspector)
                    --EditorGUI.indentLevel;
            }

            if (fromInspector)
                EditorPrefs.SetBool(str1, showOptions);
            if (!EditorGUI.EndChangeCheck())
                return false;
            serializedObject.ApplyModifiedProperties();
            return true;
        }
    }
}