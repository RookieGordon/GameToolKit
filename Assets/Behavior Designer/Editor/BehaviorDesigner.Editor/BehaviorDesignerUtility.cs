// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.BehaviorDesignerUtility
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BehaviorDesigner.Editor
{
  public static class BehaviorDesignerUtility
  {
    public const string Version = "1.7.7";
    public const int ToolBarHeight = 18;
    public const int PropertyBoxWidth = 300;
    public const int ScrollBarSize = 15;
    public const int EditorWindowTabHeight = 21;
    public const int PreferencesPaneWidth = 290;
    public const int PreferencesPaneHeight = 434;
    public const int FindDialogueWidth = 300;
    public const int FindDialogueHeight = 88;
    public const int QuickTaskListWidth = 200;
    public const int QuickTaskListHeight = 200;
    public const float GraphZoomMax = 1f;
    public const float GraphZoomMin = 0.2f;
    public const float GraphZoomSensitivity = 150f;
    public const float GraphAutoScrollEdgeDistance = 15f;
    public const float GraphAutoScrollEdgeSpeed = 3f;
    public const int LineSelectionThreshold = 7;
    public const int TaskBackgroundShadowSize = 3;
    public const int TitleHeight = 20;
    public const int TitleCompactHeight = 28;
    public const int IconAreaHeight = 52;
    public const int IconSize = 44;
    public const int IconBorderSize = 46;
    public const int CompactAreaHeight = 22;
    public const int ConnectionWidth = 42;
    public const int TopConnectionHeight = 14;
    public const int BottomConnectionHeight = 16;
    public const int TaskConnectionCollapsedWidth = 26;
    public const int TaskConnectionCollapsedHeight = 6;
    public const int MinWidth = 100;
    public const int MaxWidth = 220;
    public const int MaxCommentHeight = 100;
    public const int TextPadding = 20;
    public const float NodeFadeDuration = 0.5f;
    public const int IdentifyUpdateFadeTime = 500;
    public const int MaxIdentifyUpdateCount = 2000;
    public const float InterruptTaskHighlightDuration = 0.75f;
    public const int TaskPropertiesLabelWidth = 150;
    public const int MaxTaskDescriptionBoxWidth = 400;
    public const int MaxTaskDescriptionBoxHeight = 300;
    public const int MinorGridTickSpacing = 10;
    public const int MajorGridTickSpacing = 50;
    public const float UpdateCheckInterval = 1f;
    private static GUIStyle graphStatusGUIStyle = (GUIStyle) null;
    private static GUIStyle taskFoldoutGUIStyle = (GUIStyle) null;
    private static GUIStyle taskTitleGUIStyle = (GUIStyle) null;
    private static GUIStyle[] taskGUIStyle = new GUIStyle[9];
    private static GUIStyle[] taskCompactGUIStyle = new GUIStyle[9];
    private static GUIStyle[] taskSelectedGUIStyle = new GUIStyle[9];
    private static GUIStyle[] taskSelectedCompactGUIStyle = new GUIStyle[9];
    private static GUIStyle taskRunningGUIStyle = (GUIStyle) null;
    private static GUIStyle taskRunningCompactGUIStyle = (GUIStyle) null;
    private static GUIStyle taskRunningSelectedGUIStyle = (GUIStyle) null;
    private static GUIStyle taskRunningSelectedCompactGUIStyle = (GUIStyle) null;
    private static GUIStyle taskIdentifyGUIStyle = (GUIStyle) null;
    private static GUIStyle taskIdentifyCompactGUIStyle = (GUIStyle) null;
    private static GUIStyle taskIdentifySelectedGUIStyle = (GUIStyle) null;
    private static GUIStyle taskIdentifySelectedCompactGUIStyle = (GUIStyle) null;
    private static GUIStyle taskHighlightGUIStyle = (GUIStyle) null;
    private static GUIStyle taskHighlightCompactGUIStyle = (GUIStyle) null;
    private static GUIStyle taskCommentGUIStyle = (GUIStyle) null;
    private static GUIStyle taskCommentLeftAlignGUIStyle = (GUIStyle) null;
    private static GUIStyle taskCommentRightAlignGUIStyle = (GUIStyle) null;
    private static GUIStyle taskDescriptionGUIStyle = (GUIStyle) null;
    private static GUIStyle graphBackgroundGUIStyle = (GUIStyle) null;
    private static GUIStyle selectionGUIStyle = (GUIStyle) null;
    private static GUIStyle sharedVariableToolbarPopup = (GUIStyle) null;
    private static GUIStyle labelWrapGUIStyle = (GUIStyle) null;
    private static GUIStyle labelTitleGUIStyle = (GUIStyle) null;
    private static GUIStyle boldLabelGUIStyle = (GUIStyle) null;
    private static GUIStyle toolbarButtonLeftAlignGUIStyle = (GUIStyle) null;
    private static GUIStyle toolbarLabelGUIStyle = (GUIStyle) null;
    private static GUIStyle taskInspectorCommentGUIStyle = (GUIStyle) null;
    private static GUIStyle taskInspectorGUIStyle = (GUIStyle) null;
    private static GUIStyle toolbarButtonSelectionGUIStyle = (GUIStyle) null;
    private static GUIStyle propertyBoxGUIStyle = (GUIStyle) null;
    private static GUIStyle preferencesPaneGUIStyle = (GUIStyle) null;
    private static GUIStyle plainButtonGUIStyle = (GUIStyle) null;
    private static GUIStyle transparentButtonGUIStyle = (GUIStyle) null;
    private static GUIStyle transparentButtonOffsetGUIStyle = (GUIStyle) null;
    private static GUIStyle buttonGUIStyle = (GUIStyle) null;
    private static GUIStyle plainTextureGUIStyle = (GUIStyle) null;
    private static GUIStyle selectedBackgroundGUIStyle = (GUIStyle) null;
    private static GUIStyle errorListDarkBackground = (GUIStyle) null;
    private static GUIStyle errorListLightBackground = (GUIStyle) null;
    private static GUIStyle welcomeScreenIntroGUIStyle = (GUIStyle) null;
    private static GUIStyle welcomeScreenTextHeaderGUIStyle = (GUIStyle) null;
    private static GUIStyle welcomeScreenTextDescriptionGUIStyle = (GUIStyle) null;
    private static Texture2D[] taskBorderTexture = new Texture2D[9];
    private static Texture2D taskBorderRunningTexture = (Texture2D) null;
    private static Texture2D taskBorderIdentifyTexture = (Texture2D) null;
    private static Texture2D[] taskConnectionTopTexture = new Texture2D[9];
    private static Texture2D[] taskConnectionBottomTexture = new Texture2D[9];
    private static Texture2D taskConnectionRunningTopTexture = (Texture2D) null;
    private static Texture2D taskConnectionRunningBottomTexture = (Texture2D) null;
    private static Texture2D taskConnectionIdentifyTopTexture = (Texture2D) null;
    private static Texture2D taskConnectionIdentifyBottomTexture = (Texture2D) null;
    private static Texture2D taskConnectionCollapsedTexture = (Texture2D) null;
    private static Texture2D contentSeparatorTexture = (Texture2D) null;
    private static Texture2D docTexture = (Texture2D) null;
    private static Texture2D gearTexture = (Texture2D) null;
    private static Texture2D[] colorSelectorTexture = new Texture2D[9];
    private static Texture2D variableButtonTexture = (Texture2D) null;
    private static Texture2D variableButtonSelectedTexture = (Texture2D) null;
    private static Texture2D variableWatchButtonTexture = (Texture2D) null;
    private static Texture2D variableWatchButtonSelectedTexture = (Texture2D) null;
    private static Texture2D referencedTexture = (Texture2D) null;
    private static Texture2D conditionalAbortSelfTexture = (Texture2D) null;
    private static Texture2D conditionalAbortLowerPriorityTexture = (Texture2D) null;
    private static Texture2D conditionalAbortBothTexture = (Texture2D) null;
    private static Texture2D deleteButtonTexture = (Texture2D) null;
    private static Texture2D variableDeleteButtonTexture = (Texture2D) null;
    private static Texture2D downArrowButtonTexture = (Texture2D) null;
    private static Texture2D upArrowButtonTexture = (Texture2D) null;
    private static Texture2D variableMapButtonTexture = (Texture2D) null;
    private static Texture2D identifyButtonTexture = (Texture2D) null;
    private static Texture2D breakpointTexture = (Texture2D) null;
    private static Texture2D errorIconTexture = (Texture2D) null;
    private static Texture2D smallErrorIconTexture = (Texture2D) null;
    private static Texture2D enableTaskTexture = (Texture2D) null;
    private static Texture2D disableTaskTexture = (Texture2D) null;
    private static Texture2D expandTaskTexture = (Texture2D) null;
    private static Texture2D collapseTaskTexture = (Texture2D) null;
    private static Texture2D executionSuccessTexture = (Texture2D) null;
    private static Texture2D executionFailureTexture = (Texture2D) null;
    private static Texture2D executionSuccessRepeatTexture = (Texture2D) null;
    private static Texture2D executionFailureRepeatTexture = (Texture2D) null;
    public static Texture2D historyBackwardTexture = (Texture2D) null;
    public static Texture2D historyForwardTexture = (Texture2D) null;
    private static Texture2D playTexture = (Texture2D) null;
    private static Texture2D pauseTexture = (Texture2D) null;
    private static Texture2D stepTexture = (Texture2D) null;
    private static Texture2D screenshotBackgroundTexture = (Texture2D) null;
    private static Regex camelCaseRegex = new Regex("(?<=[A-Z])(?=[A-Z][a-z])|(?<=[^A-Z])(?=[A-Z])|(?<=[A-Za-z])(?=[^A-Za-z])", RegexOptions.IgnorePatternWhitespace);
    private static Dictionary<string, string> camelCaseSplit = new Dictionary<string, string>();
    [NonSerialized]
    private static Dictionary<System.Type, Dictionary<FieldInfo, bool>> attributeFieldCache = new Dictionary<System.Type, Dictionary<FieldInfo, bool>>();
    private static Dictionary<string, Texture2D> textureCache = new Dictionary<string, Texture2D>();
    private static Dictionary<string, Texture2D> iconCache = new Dictionary<string, Texture2D>();
    private static string BuiltinResources = "Resources/unity_builtin_extra";

    public static GUIStyle GraphStatusGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.graphStatusGUIStyle == null)
          BehaviorDesignerUtility.InitGraphStatusGUIStyle();
        return BehaviorDesignerUtility.graphStatusGUIStyle;
      }
    }

    public static GUIStyle TaskFoldoutGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskFoldoutGUIStyle == null)
          BehaviorDesignerUtility.InitTaskFoldoutGUIStyle();
        return BehaviorDesignerUtility.taskFoldoutGUIStyle;
      }
    }

    public static GUIStyle TaskTitleGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskTitleGUIStyle == null)
          BehaviorDesignerUtility.InitTaskTitleGUIStyle();
        return BehaviorDesignerUtility.taskTitleGUIStyle;
      }
    }

    public static GUIStyle GetTaskGUIStyle(int colorIndex)
    {
      if (BehaviorDesignerUtility.taskGUIStyle[colorIndex] == null)
        BehaviorDesignerUtility.InitTaskGUIStyle(colorIndex);
      return BehaviorDesignerUtility.taskGUIStyle[colorIndex];
    }

    public static GUIStyle GetTaskCompactGUIStyle(int colorIndex)
    {
      if (BehaviorDesignerUtility.taskCompactGUIStyle[colorIndex] == null)
        BehaviorDesignerUtility.InitTaskCompactGUIStyle(colorIndex);
      return BehaviorDesignerUtility.taskCompactGUIStyle[colorIndex];
    }

    public static GUIStyle GetTaskSelectedGUIStyle(int colorIndex)
    {
      if (BehaviorDesignerUtility.taskSelectedGUIStyle[colorIndex] == null)
        BehaviorDesignerUtility.InitTaskSelectedGUIStyle(colorIndex);
      return BehaviorDesignerUtility.taskSelectedGUIStyle[colorIndex];
    }

    public static GUIStyle GetTaskSelectedCompactGUIStyle(int colorIndex)
    {
      if (BehaviorDesignerUtility.taskSelectedCompactGUIStyle[colorIndex] == null)
        BehaviorDesignerUtility.InitTaskSelectedCompactGUIStyle(colorIndex);
      return BehaviorDesignerUtility.taskSelectedCompactGUIStyle[colorIndex];
    }

    public static GUIStyle TaskRunningGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskRunningGUIStyle == null)
          BehaviorDesignerUtility.InitTaskRunningGUIStyle();
        return BehaviorDesignerUtility.taskRunningGUIStyle;
      }
    }

    public static GUIStyle TaskRunningCompactGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskRunningCompactGUIStyle == null)
          BehaviorDesignerUtility.InitTaskRunningCompactGUIStyle();
        return BehaviorDesignerUtility.taskRunningCompactGUIStyle;
      }
    }

    public static GUIStyle TaskRunningSelectedGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskRunningSelectedGUIStyle == null)
          BehaviorDesignerUtility.InitTaskRunningSelectedGUIStyle();
        return BehaviorDesignerUtility.taskRunningSelectedGUIStyle;
      }
    }

    public static GUIStyle TaskRunningSelectedCompactGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskRunningSelectedCompactGUIStyle == null)
          BehaviorDesignerUtility.InitTaskRunningSelectedCompactGUIStyle();
        return BehaviorDesignerUtility.taskRunningSelectedCompactGUIStyle;
      }
    }

    public static GUIStyle TaskIdentifyGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskIdentifyGUIStyle == null)
          BehaviorDesignerUtility.InitTaskIdentifyGUIStyle();
        return BehaviorDesignerUtility.taskIdentifyGUIStyle;
      }
    }

    public static GUIStyle TaskIdentifyCompactGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskIdentifyCompactGUIStyle == null)
          BehaviorDesignerUtility.InitTaskIdentifyCompactGUIStyle();
        return BehaviorDesignerUtility.taskIdentifyCompactGUIStyle;
      }
    }

    public static GUIStyle TaskIdentifySelectedGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskIdentifySelectedGUIStyle == null)
          BehaviorDesignerUtility.InitTaskIdentifySelectedGUIStyle();
        return BehaviorDesignerUtility.taskIdentifySelectedGUIStyle;
      }
    }

    public static GUIStyle TaskIdentifySelectedCompactGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskIdentifySelectedCompactGUIStyle == null)
          BehaviorDesignerUtility.InitTaskIdentifySelectedCompactGUIStyle();
        return BehaviorDesignerUtility.taskIdentifySelectedCompactGUIStyle;
      }
    }

    public static GUIStyle TaskHighlightGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskHighlightGUIStyle == null)
          BehaviorDesignerUtility.InitTaskHighlightGUIStyle();
        return BehaviorDesignerUtility.taskHighlightGUIStyle;
      }
    }

    public static GUIStyle TaskHighlightCompactGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskHighlightCompactGUIStyle == null)
          BehaviorDesignerUtility.InitTaskHighlightCompactGUIStyle();
        return BehaviorDesignerUtility.taskHighlightCompactGUIStyle;
      }
    }

    public static GUIStyle TaskCommentGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskCommentGUIStyle == null)
          BehaviorDesignerUtility.InitTaskCommentGUIStyle();
        return BehaviorDesignerUtility.taskCommentGUIStyle;
      }
    }

    public static GUIStyle TaskCommentLeftAlignGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskCommentLeftAlignGUIStyle == null)
          BehaviorDesignerUtility.InitTaskCommentLeftAlignGUIStyle();
        return BehaviorDesignerUtility.taskCommentLeftAlignGUIStyle;
      }
    }

    public static GUIStyle TaskCommentRightAlignGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskCommentRightAlignGUIStyle == null)
          BehaviorDesignerUtility.InitTaskCommentRightAlignGUIStyle();
        return BehaviorDesignerUtility.taskCommentRightAlignGUIStyle;
      }
    }

    public static GUIStyle TaskDescriptionGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskDescriptionGUIStyle == null)
          BehaviorDesignerUtility.InitTaskDescriptionGUIStyle();
        return BehaviorDesignerUtility.taskDescriptionGUIStyle;
      }
    }

    public static GUIStyle GraphBackgroundGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.graphBackgroundGUIStyle == null)
          BehaviorDesignerUtility.InitGraphBackgroundGUIStyle();
        return BehaviorDesignerUtility.graphBackgroundGUIStyle;
      }
    }

    public static GUIStyle SelectionGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.selectionGUIStyle == null)
          BehaviorDesignerUtility.InitSelectionGUIStyle();
        return BehaviorDesignerUtility.selectionGUIStyle;
      }
    }

    public static GUIStyle SharedVariableToolbarPopup
    {
      get
      {
        if (BehaviorDesignerUtility.sharedVariableToolbarPopup == null)
          BehaviorDesignerUtility.InitSharedVariableToolbarPopup();
        return BehaviorDesignerUtility.sharedVariableToolbarPopup;
      }
    }

    public static GUIStyle LabelWrapGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.labelWrapGUIStyle == null)
          BehaviorDesignerUtility.InitLabelWrapGUIStyle();
        return BehaviorDesignerUtility.labelWrapGUIStyle;
      }
    }

    public static GUIStyle LabelTitleGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.labelTitleGUIStyle == null)
          BehaviorDesignerUtility.InitLabelTitleGUIStyle();
        return BehaviorDesignerUtility.labelTitleGUIStyle;
      }
    }

    public static GUIStyle BoldLabelGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.boldLabelGUIStyle == null)
          BehaviorDesignerUtility.InitBoldLabelGUIStyle();
        return BehaviorDesignerUtility.boldLabelGUIStyle;
      }
    }

    public static GUIStyle ToolbarButtonLeftAlignGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.toolbarButtonLeftAlignGUIStyle == null)
          BehaviorDesignerUtility.InitToolbarButtonLeftAlignGUIStyle();
        return BehaviorDesignerUtility.toolbarButtonLeftAlignGUIStyle;
      }
    }

    public static GUIStyle ToolbarLabelGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.toolbarLabelGUIStyle == null)
          BehaviorDesignerUtility.InitToolbarLabelGUIStyle();
        return BehaviorDesignerUtility.toolbarLabelGUIStyle;
      }
    }

    public static GUIStyle TaskInspectorCommentGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskInspectorCommentGUIStyle == null)
          BehaviorDesignerUtility.InitTaskInspectorCommentGUIStyle();
        return BehaviorDesignerUtility.taskInspectorCommentGUIStyle;
      }
    }

    public static GUIStyle TaskInspectorGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.taskInspectorGUIStyle == null)
          BehaviorDesignerUtility.InitTaskInspectorGUIStyle();
        return BehaviorDesignerUtility.taskInspectorGUIStyle;
      }
    }

    public static GUIStyle ToolbarButtonSelectionGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.toolbarButtonSelectionGUIStyle == null)
          BehaviorDesignerUtility.InitToolbarButtonSelectionGUIStyle();
        return BehaviorDesignerUtility.toolbarButtonSelectionGUIStyle;
      }
    }

    public static GUIStyle PropertyBoxGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.propertyBoxGUIStyle == null)
          BehaviorDesignerUtility.InitPropertyBoxGUIStyle();
        return BehaviorDesignerUtility.propertyBoxGUIStyle;
      }
    }

    public static GUIStyle PreferencesPaneGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.preferencesPaneGUIStyle == null)
          BehaviorDesignerUtility.InitPreferencesPaneGUIStyle();
        return BehaviorDesignerUtility.preferencesPaneGUIStyle;
      }
    }

    public static GUIStyle PlainButtonGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.plainButtonGUIStyle == null)
          BehaviorDesignerUtility.InitPlainButtonGUIStyle();
        return BehaviorDesignerUtility.plainButtonGUIStyle;
      }
    }

    public static GUIStyle TransparentButtonGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.transparentButtonGUIStyle == null)
          BehaviorDesignerUtility.InitTransparentButtonGUIStyle();
        return BehaviorDesignerUtility.transparentButtonGUIStyle;
      }
    }

    public static GUIStyle TransparentButtonOffsetGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.transparentButtonOffsetGUIStyle == null)
          BehaviorDesignerUtility.InitTransparentButtonOffsetGUIStyle();
        return BehaviorDesignerUtility.transparentButtonOffsetGUIStyle;
      }
    }

    public static GUIStyle ButtonGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.buttonGUIStyle == null)
          BehaviorDesignerUtility.InitButtonGUIStyle();
        return BehaviorDesignerUtility.buttonGUIStyle;
      }
    }

    public static GUIStyle PlainTextureGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.plainTextureGUIStyle == null)
          BehaviorDesignerUtility.InitPlainTextureGUIStyle();
        return BehaviorDesignerUtility.plainTextureGUIStyle;
      }
    }

    public static GUIStyle SelectedBackgroundGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.selectedBackgroundGUIStyle == null)
          BehaviorDesignerUtility.InitSelectedBackgroundGUIStyle();
        return BehaviorDesignerUtility.selectedBackgroundGUIStyle;
      }
    }

    public static GUIStyle ErrorListDarkBackground
    {
      get
      {
        if (BehaviorDesignerUtility.errorListDarkBackground == null)
          BehaviorDesignerUtility.InitErrorListDarkBackground();
        return BehaviorDesignerUtility.errorListDarkBackground;
      }
    }

    public static GUIStyle ErrorListLightBackground
    {
      get
      {
        if (BehaviorDesignerUtility.errorListLightBackground == null)
          BehaviorDesignerUtility.InitErrorListLightBackground();
        return BehaviorDesignerUtility.errorListLightBackground;
      }
    }

    public static GUIStyle WelcomeScreenIntroGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.welcomeScreenIntroGUIStyle == null)
          BehaviorDesignerUtility.InitWelcomeScreenIntroGUIStyle();
        return BehaviorDesignerUtility.welcomeScreenIntroGUIStyle;
      }
    }

    public static GUIStyle WelcomeScreenTextHeaderGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.welcomeScreenTextHeaderGUIStyle == null)
          BehaviorDesignerUtility.InitWelcomeScreenTextHeaderGUIStyle();
        return BehaviorDesignerUtility.welcomeScreenTextHeaderGUIStyle;
      }
    }

    public static GUIStyle WelcomeScreenTextDescriptionGUIStyle
    {
      get
      {
        if (BehaviorDesignerUtility.welcomeScreenTextDescriptionGUIStyle == null)
          BehaviorDesignerUtility.InitWelcomeScreenTextDescriptionGUIStyle();
        return BehaviorDesignerUtility.welcomeScreenTextDescriptionGUIStyle;
      }
    }

    public static Texture2D GetTaskBorderTexture(int colorIndex)
    {
      if ((UnityEngine.Object) BehaviorDesignerUtility.taskBorderTexture[colorIndex] == (UnityEngine.Object) null)
        BehaviorDesignerUtility.InitTaskBorderTexture(colorIndex);
      return BehaviorDesignerUtility.taskBorderTexture[colorIndex];
    }

    public static Texture2D TaskBorderRunningTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.taskBorderRunningTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitTaskBorderRunningTexture();
        return BehaviorDesignerUtility.taskBorderRunningTexture;
      }
    }

    public static Texture2D TaskBorderIdentifyTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.taskBorderIdentifyTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitTaskBorderIdentifyTexture();
        return BehaviorDesignerUtility.taskBorderIdentifyTexture;
      }
    }

    public static Texture2D GetTaskConnectionTopTexture(int colorIndex)
    {
      if ((UnityEngine.Object) BehaviorDesignerUtility.taskConnectionTopTexture[colorIndex] == (UnityEngine.Object) null)
        BehaviorDesignerUtility.InitTaskConnectionTopTexture(colorIndex);
      return BehaviorDesignerUtility.taskConnectionTopTexture[colorIndex];
    }

    public static Texture2D GetTaskConnectionBottomTexture(int colorIndex)
    {
      if ((UnityEngine.Object) BehaviorDesignerUtility.taskConnectionBottomTexture[colorIndex] == (UnityEngine.Object) null)
        BehaviorDesignerUtility.InitTaskConnectionBottomTexture(colorIndex);
      return BehaviorDesignerUtility.taskConnectionBottomTexture[colorIndex];
    }

    public static Texture2D TaskConnectionRunningTopTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.taskConnectionRunningTopTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitTaskConnectionRunningTopTexture();
        return BehaviorDesignerUtility.taskConnectionRunningTopTexture;
      }
    }

    public static Texture2D TaskConnectionRunningBottomTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.taskConnectionRunningBottomTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitTaskConnectionRunningBottomTexture();
        return BehaviorDesignerUtility.taskConnectionRunningBottomTexture;
      }
    }

    public static Texture2D TaskConnectionIdentifyTopTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.taskConnectionIdentifyTopTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitTaskConnectionIdentifyTopTexture();
        return BehaviorDesignerUtility.taskConnectionIdentifyTopTexture;
      }
    }

    public static Texture2D TaskConnectionIdentifyBottomTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.taskConnectionIdentifyBottomTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitTaskConnectionIdentifyBottomTexture();
        return BehaviorDesignerUtility.taskConnectionIdentifyBottomTexture;
      }
    }

    public static Texture2D TaskConnectionCollapsedTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.taskConnectionCollapsedTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitTaskConnectionCollapsedTexture();
        return BehaviorDesignerUtility.taskConnectionCollapsedTexture;
      }
    }

    public static Texture2D ContentSeparatorTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.contentSeparatorTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitContentSeparatorTexture();
        return BehaviorDesignerUtility.contentSeparatorTexture;
      }
    }

    public static Texture2D DocTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.docTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitDocTexture();
        return BehaviorDesignerUtility.docTexture;
      }
    }

    public static Texture2D GearTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.gearTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitGearTexture();
        return BehaviorDesignerUtility.gearTexture;
      }
    }

    public static Texture2D ColorSelectorTexture(int colorIndex)
    {
      if ((UnityEngine.Object) BehaviorDesignerUtility.colorSelectorTexture[colorIndex] == (UnityEngine.Object) null)
        BehaviorDesignerUtility.InitColorSelectorTexture(colorIndex);
      return BehaviorDesignerUtility.colorSelectorTexture[colorIndex];
    }

    public static Texture2D VariableButtonTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.variableButtonTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitVariableButtonTexture();
        return BehaviorDesignerUtility.variableButtonTexture;
      }
    }

    public static Texture2D VariableButtonSelectedTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.variableButtonSelectedTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitVariableButtonSelectedTexture();
        return BehaviorDesignerUtility.variableButtonSelectedTexture;
      }
    }

    public static Texture2D VariableWatchButtonTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.variableWatchButtonTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitVariableWatchButtonTexture();
        return BehaviorDesignerUtility.variableWatchButtonTexture;
      }
    }

    public static Texture2D VariableWatchButtonSelectedTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.variableWatchButtonSelectedTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitVariableWatchButtonSelectedTexture();
        return BehaviorDesignerUtility.variableWatchButtonSelectedTexture;
      }
    }

    public static Texture2D ReferencedTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.referencedTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitReferencedTexture();
        return BehaviorDesignerUtility.referencedTexture;
      }
    }

    public static Texture2D ConditionalAbortSelfTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.conditionalAbortSelfTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitConditionalAbortSelfTexture();
        return BehaviorDesignerUtility.conditionalAbortSelfTexture;
      }
    }

    public static Texture2D ConditionalAbortLowerPriorityTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.conditionalAbortLowerPriorityTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitConditionalAbortLowerPriorityTexture();
        return BehaviorDesignerUtility.conditionalAbortLowerPriorityTexture;
      }
    }

    public static Texture2D ConditionalAbortBothTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.conditionalAbortBothTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitConditionalAbortBothTexture();
        return BehaviorDesignerUtility.conditionalAbortBothTexture;
      }
    }

    public static Texture2D DeleteButtonTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.deleteButtonTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitDeleteButtonTexture();
        return BehaviorDesignerUtility.deleteButtonTexture;
      }
    }

    public static Texture2D VariableDeleteButtonTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.variableDeleteButtonTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitVariableDeleteButtonTexture();
        return BehaviorDesignerUtility.variableDeleteButtonTexture;
      }
    }

    public static Texture2D DownArrowButtonTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.downArrowButtonTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitDownArrowButtonTexture();
        return BehaviorDesignerUtility.downArrowButtonTexture;
      }
    }

    public static Texture2D UpArrowButtonTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.upArrowButtonTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitUpArrowButtonTexture();
        return BehaviorDesignerUtility.upArrowButtonTexture;
      }
    }

    public static Texture2D VariableMapButtonTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.variableMapButtonTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitVariableMapButtonTexture();
        return BehaviorDesignerUtility.variableMapButtonTexture;
      }
    }

    public static Texture2D IdentifyButtonTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.identifyButtonTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitIdentifyButtonTexture();
        return BehaviorDesignerUtility.identifyButtonTexture;
      }
    }

    public static Texture2D BreakpointTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.breakpointTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitBreakpointTexture();
        return BehaviorDesignerUtility.breakpointTexture;
      }
    }

    public static Texture2D ErrorIconTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.errorIconTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitErrorIconTexture();
        return BehaviorDesignerUtility.errorIconTexture;
      }
    }

    public static Texture2D SmallErrorIconTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.smallErrorIconTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitSmallErrorIconTexture();
        return BehaviorDesignerUtility.smallErrorIconTexture;
      }
    }

    public static Texture2D EnableTaskTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.enableTaskTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitEnableTaskTexture();
        return BehaviorDesignerUtility.enableTaskTexture;
      }
    }

    public static Texture2D DisableTaskTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.disableTaskTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitDisableTaskTexture();
        return BehaviorDesignerUtility.disableTaskTexture;
      }
    }

    public static Texture2D ExpandTaskTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.expandTaskTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitExpandTaskTexture();
        return BehaviorDesignerUtility.expandTaskTexture;
      }
    }

    public static Texture2D CollapseTaskTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.collapseTaskTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitCollapseTaskTexture();
        return BehaviorDesignerUtility.collapseTaskTexture;
      }
    }

    public static Texture2D ExecutionSuccessTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.executionSuccessTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitExecutionSuccessTexture();
        return BehaviorDesignerUtility.executionSuccessTexture;
      }
    }

    public static Texture2D ExecutionFailureTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.executionFailureTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitExecutionFailureTexture();
        return BehaviorDesignerUtility.executionFailureTexture;
      }
    }

    public static Texture2D ExecutionSuccessRepeatTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.executionSuccessRepeatTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitExecutionSuccessRepeatTexture();
        return BehaviorDesignerUtility.executionSuccessRepeatTexture;
      }
    }

    public static Texture2D ExecutionFailureRepeatTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.executionFailureRepeatTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitExecutionFailureRepeatTexture();
        return BehaviorDesignerUtility.executionFailureRepeatTexture;
      }
    }

    public static Texture2D HistoryBackwardTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.historyBackwardTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitHistoryBackwardTexture();
        return BehaviorDesignerUtility.historyBackwardTexture;
      }
    }

    public static Texture2D HistoryForwardTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.historyForwardTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitHistoryForwardTexture();
        return BehaviorDesignerUtility.historyForwardTexture;
      }
    }

    public static Texture2D PlayTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.playTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitPlayTexture();
        return BehaviorDesignerUtility.playTexture;
      }
    }

    public static Texture2D PauseTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.pauseTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitPauseTexture();
        return BehaviorDesignerUtility.pauseTexture;
      }
    }

    public static Texture2D StepTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.stepTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitStepTexture();
        return BehaviorDesignerUtility.stepTexture;
      }
    }

    public static Texture2D ScreenshotBackgroundTexture
    {
      get
      {
        if ((UnityEngine.Object) BehaviorDesignerUtility.screenshotBackgroundTexture == (UnityEngine.Object) null)
          BehaviorDesignerUtility.InitScreenshotBackgroundTexture();
        return BehaviorDesignerUtility.screenshotBackgroundTexture;
      }
    }

    public static string SplitCamelCase(string s)
    {
      if (s.Equals(string.Empty))
        return s;
      if (BehaviorDesignerUtility.camelCaseSplit.ContainsKey(s))
        return BehaviorDesignerUtility.camelCaseSplit[s];
      string key = s;
      s = s.Replace("_uScript", "uScript");
      s = s.Replace("_PlayMaker", "PlayMaker");
      if (s.Length > 2 && s.Substring(0, 2).CompareTo("m_") == 0)
        s = s.Substring(2);
      else if (s.Length > 1 && s[0].CompareTo('_') == 0)
        s = s.Substring(1);
      s = BehaviorDesignerUtility.camelCaseRegex.Replace(s, " ");
      s = s.Replace("_", " ");
      s = s.Replace("u Script", " uScript");
      s = s.Replace("Play Maker", "PlayMaker");
      s = (char.ToUpper(s[0]).ToString() + s.Substring(1)).Trim();
      BehaviorDesignerUtility.camelCaseSplit.Add(key, s);
      return s;
    }

    public static bool HasAttribute(FieldInfo field, System.Type attributeType)
    {
      Dictionary<FieldInfo, bool> dictionary = (Dictionary<FieldInfo, bool>) null;
      if (BehaviorDesignerUtility.attributeFieldCache.ContainsKey(attributeType))
        dictionary = BehaviorDesignerUtility.attributeFieldCache[attributeType];
      if (dictionary == null)
        dictionary = new Dictionary<FieldInfo, bool>();
      if (dictionary.ContainsKey(field))
        return dictionary[field];
      bool flag = field.GetCustomAttributes(attributeType, false).Length > 0;
      dictionary.Add(field, flag);
      if (!BehaviorDesignerUtility.attributeFieldCache.ContainsKey(attributeType))
        BehaviorDesignerUtility.attributeFieldCache.Add(attributeType, dictionary);
      return flag;
    }

    public static List<Task> GetAllTasks(BehaviorSource behaviorSource)
    {
      List<Task> taskList = new List<Task>();
      if (behaviorSource.RootTask != null)
        BehaviorDesignerUtility.GetAllTasks(behaviorSource.RootTask, ref taskList);
      if (behaviorSource.DetachedTasks != null)
      {
        for (int index = 0; index < behaviorSource.DetachedTasks.Count; ++index)
          BehaviorDesignerUtility.GetAllTasks(behaviorSource.DetachedTasks[index], ref taskList);
      }
      return taskList;
    }

    private static void GetAllTasks(Task task, ref List<Task> taskList)
    {
      taskList.Add(task);
      if (!(task is ParentTask parentTask) || parentTask.Children == null)
        return;
      for (int index = 0; index < parentTask.Children.Count; ++index)
        BehaviorDesignerUtility.GetAllTasks(parentTask.Children[index], ref taskList);
    }

    public static bool AnyNullTasks(BehaviorSource behaviorSource)
    {
      if (behaviorSource.RootTask != null && BehaviorDesignerUtility.AnyNullTasks(behaviorSource.RootTask))
        return true;
      if (behaviorSource.DetachedTasks != null)
      {
        for (int index = 0; index < behaviorSource.DetachedTasks.Count; ++index)
        {
          if (BehaviorDesignerUtility.AnyNullTasks(behaviorSource.DetachedTasks[index]))
            return true;
        }
      }
      return false;
    }

    private static bool AnyNullTasks(Task task)
    {
      if (task == null)
        return true;
      if (task is ParentTask parentTask && parentTask.Children != null)
      {
        for (int index = 0; index < parentTask.Children.Count; ++index)
        {
          if (BehaviorDesignerUtility.AnyNullTasks(parentTask.Children[index]))
            return true;
        }
      }
      return false;
    }

    public static bool HasRootTask(string serialization) => !string.IsNullOrEmpty(serialization) && MiniJSON.Deserialize(serialization) is Dictionary<string, object> dictionary && dictionary.ContainsKey("RootTask");

    public static string GetEditorBaseDirectory(UnityEngine.Object obj = null) => Path.GetDirectoryName(Uri.UnescapeDataString(new UriBuilder(Assembly.GetExecutingAssembly().CodeBase).Path).Substring(Application.dataPath.Length - 6));

    public static Texture2D LoadTexture(string imageName, bool useSkinColor = true, UnityEngine.Object obj = null)
    {
      if (BehaviorDesignerUtility.textureCache.ContainsKey(imageName))
        return BehaviorDesignerUtility.textureCache[imageName];
      Texture2D texture2D = (Texture2D) null;
      string name1 = string.Format("{0}{1}", !useSkinColor ? (object) string.Empty : (!EditorGUIUtility.isProSkin ? (object) "Light" : (object) "Dark"), (object) imageName);
      texture2D = AssetDatabase.LoadAssetAtPath<Texture2D>(Path.Combine(BuiltinResources, name1));
      if(texture2D == null){
        string name2 = string.Format("Assets/Behavior Designer/Editor/Resources/{0}{1}",
                    !useSkinColor
                        ? (object)string.Empty
                        : (!EditorGUIUtility.isProSkin ? (object)"Light" : (object)"Dark"), (object)imageName);
        texture2D = AssetDatabase.LoadAssetAtPath<Texture2D>(name2);
      }
      // Stream manifestResourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name1);
      // if (manifestResourceStream == null)
      // {
      //   string name2 = string.Format("BehaviorDesignerEditor.Resources.{0}{1}", !useSkinColor ? (object) string.Empty : (!EditorGUIUtility.isProSkin ? (object) "Light" : (object) "Dark"), (object) imageName);
      //   manifestResourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name2);
      // }
      // if (manifestResourceStream != null)
      // {
      //   texture2D = new Texture2D(1, 1, TextureFormat.RGBA32, false);
      //   ImageConversion.LoadImage(texture2D, BehaviorDesignerUtility.ReadToEnd(manifestResourceStream));
      //   manifestResourceStream.Close();
      // }
      texture2D.hideFlags = HideFlags.HideAndDontSave;
      BehaviorDesignerUtility.textureCache.Add(imageName, texture2D);
      return texture2D;
    }

    private static Texture2D LoadTaskTexture(
      string imageName,
      bool useSkinColor = true,
      ScriptableObject obj = null)
    {
      if (BehaviorDesignerUtility.textureCache.ContainsKey(imageName))
        return BehaviorDesignerUtility.textureCache[imageName];
      Texture2D texture2D = (Texture2D) null;
      string name1 = string.Format("{0}{1}", !useSkinColor ? (object) string.Empty : (!EditorGUIUtility.isProSkin ? (object) "Light" : (object) "Dark"), (object) imageName);
      texture2D = AssetDatabase.LoadAssetAtPath<Texture2D>(Path.Combine(BuiltinResources, name1));
      if (texture2D == null){
        string name2 = string.Format("Assets/Behavior Designer/Editor/Resources/{0}{1}",
                    !useSkinColor
                        ? (object)string.Empty
                        : (!EditorGUIUtility.isProSkin ? (object)"Light" : (object)"Dark"), (object)imageName);
        texture2D = AssetDatabase.LoadAssetAtPath<Texture2D>(name2);
      }
      // Stream manifestResourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name1);
      // if (manifestResourceStream == null)
      // {
      //   string name2 = string.Format("BehaviorDesignerEditor.Resources.{0}{1}", !useSkinColor ? (object) string.Empty : (!EditorGUIUtility.isProSkin ? (object) "Light" : (object) "Dark"), (object) imageName);
      //   manifestResourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name2);
      // }
      // if (manifestResourceStream != null)
      // {
      //   texture2D = new Texture2D(1, 1, TextureFormat.RGBA32, false);
      //   ImageConversion.LoadImage(texture2D, BehaviorDesignerUtility.ReadToEnd(manifestResourceStream));
      //   manifestResourceStream.Close();
      // }
      if ((UnityEngine.Object) texture2D == (UnityEngine.Object) null)
        Debug.Log((object) string.Format("{0}/Images/Task Backgrounds/{1}{2}", (object) BehaviorDesignerUtility.GetEditorBaseDirectory((UnityEngine.Object) obj), !useSkinColor ? (object) string.Empty : (!EditorGUIUtility.isProSkin ? (object) "Light" : (object) "Dark"), (object) imageName));
      texture2D.hideFlags = HideFlags.HideAndDontSave;
      BehaviorDesignerUtility.textureCache.Add(imageName, texture2D);
      return texture2D;
    }

    public static Texture2D LoadIcon(string iconName, ScriptableObject obj = null)
    {
      if (BehaviorDesignerUtility.iconCache.ContainsKey(iconName))
        return BehaviorDesignerUtility.iconCache[iconName];
      Texture2D texture2D = (Texture2D) null;
      string name1 = iconName.Replace("{SkinColor}", !EditorGUIUtility.isProSkin ? "Light" : "Dark");
      texture2D = AssetDatabase.LoadAssetAtPath<Texture2D>(Path.Combine(BuiltinResources, name1));
      if(texture2D == null){
        string name2 = string.Format("Assets/Behavior Designer/Editor/Resources/{0}",
                    (object)iconName.Replace("{SkinColor}", !EditorGUIUtility.isProSkin ? "Light" : "Dark"));
        texture2D = AssetDatabase.LoadAssetAtPath<Texture2D>(name2);
      }
      // Stream manifestResourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name1);
      // if (manifestResourceStream == null)
      // {
      //   string name2 = string.Format("BehaviorDesignerEditor.Resources.{0}", (object) iconName.Replace("{SkinColor}", !EditorGUIUtility.isProSkin ? "Light" : "Dark"));
      //   manifestResourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name2);
      // }
      // if (manifestResourceStream != null)
      // {
      //   texture2D = new Texture2D(1, 1, TextureFormat.RGBA32, false);
      //   ImageConversion.LoadImage(texture2D, BehaviorDesignerUtility.ReadToEnd(manifestResourceStream));
      //   manifestResourceStream.Close();
      // }
      if ((UnityEngine.Object) texture2D == (UnityEngine.Object) null)
        texture2D = AssetDatabase.LoadAssetAtPath(iconName.Replace("{SkinColor}", !EditorGUIUtility.isProSkin ? "Light" : "Dark"), typeof (Texture2D)) as Texture2D;
      if ((UnityEngine.Object) texture2D != (UnityEngine.Object) null)
        texture2D.hideFlags = HideFlags.HideAndDontSave;
      BehaviorDesignerUtility.iconCache.Add(iconName, texture2D);
      return texture2D;
    }

    private static byte[] ReadToEnd(Stream stream)
    {
      byte[] buffer = new byte[16384];
      using (MemoryStream memoryStream = new MemoryStream())
      {
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
          memoryStream.Write(buffer, 0, count);
        return memoryStream.ToArray();
      }
    }

    public static void DrawContentSeperator(int yOffset) => BehaviorDesignerUtility.DrawContentSeperator(yOffset, 0);

    public static void DrawContentSeperator(int yOffset, int widthExtension)
    {
      Rect lastRect = GUILayoutUtility.GetLastRect();
      lastRect.x = -5f;
      lastRect.y += lastRect.height + (float) yOffset;
      lastRect.height = 2f;
      lastRect.width += (float) (10 + widthExtension);
      GUI.DrawTexture(lastRect, (Texture) BehaviorDesignerUtility.ContentSeparatorTexture);
    }

    public static float RoundToNearest(float num, float baseNum) => (float) (int) Math.Round((double) num / (double) baseNum, MidpointRounding.AwayFromZero) * baseNum;

    private static void InitGraphStatusGUIStyle()
    {
      BehaviorDesignerUtility.graphStatusGUIStyle = new GUIStyle(GUI.skin.label);
      BehaviorDesignerUtility.graphStatusGUIStyle.alignment = (TextAnchor) 3;
      BehaviorDesignerUtility.graphStatusGUIStyle.fontSize = 20;
      BehaviorDesignerUtility.graphStatusGUIStyle.fontStyle = (FontStyle) 1;
      if (EditorGUIUtility.isProSkin)
        BehaviorDesignerUtility.graphStatusGUIStyle.normal.textColor = new Color(0.7058f, 0.7058f, 0.7058f);
      else
        BehaviorDesignerUtility.graphStatusGUIStyle.normal.textColor = new Color(0.8058f, 0.8058f, 0.8058f);
    }

    private static void InitTaskFoldoutGUIStyle()
    {
      BehaviorDesignerUtility.taskFoldoutGUIStyle = new GUIStyle(EditorStyles.foldout);
      BehaviorDesignerUtility.taskFoldoutGUIStyle.alignment = (TextAnchor) 3;
      BehaviorDesignerUtility.taskFoldoutGUIStyle.fontSize = 13;
      BehaviorDesignerUtility.taskFoldoutGUIStyle.fontStyle = (FontStyle) 1;
    }

    private static void InitTaskTitleGUIStyle()
    {
      BehaviorDesignerUtility.taskTitleGUIStyle = new GUIStyle(GUI.skin.label);
      BehaviorDesignerUtility.taskTitleGUIStyle.alignment = (TextAnchor) 1;
      BehaviorDesignerUtility.taskTitleGUIStyle.fontSize = 12;
      BehaviorDesignerUtility.taskTitleGUIStyle.fontStyle = (FontStyle) 0;
    }

    private static void InitTaskGUIStyle(int colorIndex) => BehaviorDesignerUtility.taskGUIStyle[colorIndex] = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("Task" + BehaviorDesignerUtility.ColorIndexToColorString(colorIndex) + ".png"), new RectOffset(5, 3, 3, 5));

    private static void InitTaskCompactGUIStyle(int colorIndex) => BehaviorDesignerUtility.taskCompactGUIStyle[colorIndex] = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("TaskCompact" + BehaviorDesignerUtility.ColorIndexToColorString(colorIndex) + ".png"), new RectOffset(5, 4, 4, 5));

    private static void InitTaskSelectedGUIStyle(int colorIndex) => BehaviorDesignerUtility.taskSelectedGUIStyle[colorIndex] = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("TaskSelected" + BehaviorDesignerUtility.ColorIndexToColorString(colorIndex) + ".png"), new RectOffset(5, 4, 4, 4));

    private static void InitTaskSelectedCompactGUIStyle(int colorIndex) => BehaviorDesignerUtility.taskSelectedCompactGUIStyle[colorIndex] = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("TaskSelectedCompact" + BehaviorDesignerUtility.ColorIndexToColorString(colorIndex) + ".png"), new RectOffset(5, 4, 4, 4));

    private static string ColorIndexToColorString(int index)
    {
      switch (index)
      {
        case 0:
          return string.Empty;
        case 1:
          return "Red";
        case 2:
          return "Pink";
        case 3:
          return "Brown";
        case 4:
          return "RedOrange";
        case 5:
          return "Turquoise";
        case 6:
          return "Cyan";
        case 7:
          return "Blue";
        case 8:
          return "Purple";
        default:
          return string.Empty;
      }
    }

    private static void InitTaskRunningGUIStyle() => BehaviorDesignerUtility.taskRunningGUIStyle = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("TaskRunning.png"), new RectOffset(5, 3, 3, 5));

    private static void InitTaskRunningCompactGUIStyle() => BehaviorDesignerUtility.taskRunningCompactGUIStyle = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("TaskRunningCompact.png"), new RectOffset(5, 4, 4, 5));

    private static void InitTaskRunningSelectedGUIStyle() => BehaviorDesignerUtility.taskRunningSelectedGUIStyle = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("TaskRunningSelected.png"), new RectOffset(5, 4, 4, 4));

    private static void InitTaskRunningSelectedCompactGUIStyle() => BehaviorDesignerUtility.taskRunningSelectedCompactGUIStyle = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("TaskRunningSelectedCompact.png"), new RectOffset(5, 4, 4, 4));

    private static void InitTaskIdentifyGUIStyle() => BehaviorDesignerUtility.taskIdentifyGUIStyle = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("TaskIdentify.png"), new RectOffset(5, 3, 3, 5));

    private static void InitTaskIdentifyCompactGUIStyle() => BehaviorDesignerUtility.taskIdentifyCompactGUIStyle = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("TaskIdentifyCompact.png"), new RectOffset(5, 4, 4, 5));

    private static void InitTaskIdentifySelectedGUIStyle() => BehaviorDesignerUtility.taskIdentifySelectedGUIStyle = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("TaskIdentifySelected.png"), new RectOffset(5, 4, 4, 4));

    private static void InitTaskIdentifySelectedCompactGUIStyle() => BehaviorDesignerUtility.taskIdentifySelectedCompactGUIStyle = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("TaskIdentifySelectedCompact.png"), new RectOffset(5, 4, 4, 4));

    private static void InitTaskHighlightGUIStyle() => BehaviorDesignerUtility.taskHighlightGUIStyle = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("TaskHighlight.png"), new RectOffset(5, 4, 4, 4));

    private static void InitTaskHighlightCompactGUIStyle() => BehaviorDesignerUtility.taskHighlightCompactGUIStyle = BehaviorDesignerUtility.InitTaskGUIStyle(BehaviorDesignerUtility.LoadTaskTexture("TaskHighlightCompact.png"), new RectOffset(5, 4, 4, 4));

    private static GUIStyle InitTaskGUIStyle(Texture2D texture, RectOffset overflow)
    {
      GUIStyle guiStyle = new GUIStyle()
      {
        border = new RectOffset(10, 10, 10, 10),
        overflow = overflow,
        normal = {
          background = texture
        },
        active = {
          background = texture
        },
        hover = {
          background = texture
        },
        focused = {
          background = texture
        }
      };
      guiStyle.normal.textColor = Color.white;
      guiStyle.active.textColor = Color.white;
      guiStyle.hover.textColor = Color.white;
      guiStyle.focused.textColor = Color.white;
      guiStyle.stretchHeight = true;
      guiStyle.stretchWidth = true;
      return guiStyle;
    }

    private static void InitTaskCommentGUIStyle()
    {
      BehaviorDesignerUtility.taskCommentGUIStyle = new GUIStyle(GUI.skin.label);
      BehaviorDesignerUtility.taskCommentGUIStyle.alignment = (TextAnchor) 1;
      BehaviorDesignerUtility.taskCommentGUIStyle.fontSize = 12;
      BehaviorDesignerUtility.taskCommentGUIStyle.fontStyle = (FontStyle) 0;
      BehaviorDesignerUtility.taskCommentGUIStyle.wordWrap = true;
    }

    private static void InitTaskCommentLeftAlignGUIStyle()
    {
      BehaviorDesignerUtility.taskCommentLeftAlignGUIStyle = new GUIStyle(GUI.skin.label);
      BehaviorDesignerUtility.taskCommentLeftAlignGUIStyle.alignment = (TextAnchor) 0;
      BehaviorDesignerUtility.taskCommentLeftAlignGUIStyle.fontSize = 12;
      BehaviorDesignerUtility.taskCommentLeftAlignGUIStyle.fontStyle = (FontStyle) 0;
      BehaviorDesignerUtility.taskCommentLeftAlignGUIStyle.wordWrap = false;
    }

    private static void InitTaskCommentRightAlignGUIStyle()
    {
      BehaviorDesignerUtility.taskCommentRightAlignGUIStyle = new GUIStyle(GUI.skin.label);
      BehaviorDesignerUtility.taskCommentRightAlignGUIStyle.alignment = (TextAnchor) 2;
      BehaviorDesignerUtility.taskCommentRightAlignGUIStyle.fontSize = 12;
      BehaviorDesignerUtility.taskCommentRightAlignGUIStyle.fontStyle = (FontStyle) 0;
      BehaviorDesignerUtility.taskCommentRightAlignGUIStyle.wordWrap = false;
    }

    private static void InitTaskDescriptionGUIStyle()
    {
      Texture2D texture2D = new Texture2D(1, 1, TextureFormat.RGBA32, false);
      if (EditorGUIUtility.isProSkin)
        texture2D.SetPixel(1, 1, new Color(0.1647f, 0.1647f, 0.1647f));
      else
        texture2D.SetPixel(1, 1, new Color(0.75f, 0.75f, 0.75f));
      texture2D.hideFlags = HideFlags.HideAndDontSave;
      texture2D.Apply();
      BehaviorDesignerUtility.taskDescriptionGUIStyle = new GUIStyle();
      BehaviorDesignerUtility.taskDescriptionGUIStyle.normal.background = texture2D;
      BehaviorDesignerUtility.taskDescriptionGUIStyle.active.background = texture2D;
      BehaviorDesignerUtility.taskDescriptionGUIStyle.hover.background = texture2D;
      BehaviorDesignerUtility.taskDescriptionGUIStyle.focused.background = texture2D;
    }

    private static void InitGraphBackgroundGUIStyle()
    {
      Texture2D texture2D = new Texture2D(1, 1, TextureFormat.RGBA32, false);
      if (EditorGUIUtility.isProSkin)
        texture2D.SetPixel(1, 1, new Color(0.1647f, 0.1647f, 0.1647f));
      else
        texture2D.SetPixel(1, 1, new Color(0.3647f, 0.3647f, 0.3647f));
      texture2D.hideFlags = HideFlags.HideAndDontSave;
      texture2D.Apply();
      BehaviorDesignerUtility.graphBackgroundGUIStyle = new GUIStyle();
      BehaviorDesignerUtility.graphBackgroundGUIStyle.normal.background = texture2D;
      BehaviorDesignerUtility.graphBackgroundGUIStyle.active.background = texture2D;
      BehaviorDesignerUtility.graphBackgroundGUIStyle.hover.background = texture2D;
      BehaviorDesignerUtility.graphBackgroundGUIStyle.focused.background = texture2D;
    }

    private static void InitSelectionGUIStyle()
    {
      Texture2D texture2D = new Texture2D(1, 1, TextureFormat.RGBA32, false);
      Color color = !EditorGUIUtility.isProSkin ? new Color(0.243f, 0.5686f, 0.839f, 0.5f) : new Color(0.188f, 0.4588f, 0.6862f, 0.5f);
      texture2D.SetPixel(1, 1, color);
      texture2D.hideFlags = HideFlags.HideAndDontSave;
      texture2D.Apply();
      BehaviorDesignerUtility.selectionGUIStyle = new GUIStyle();
      BehaviorDesignerUtility.selectionGUIStyle.normal.background = texture2D;
      BehaviorDesignerUtility.selectionGUIStyle.active.background = texture2D;
      BehaviorDesignerUtility.selectionGUIStyle.hover.background = texture2D;
      BehaviorDesignerUtility.selectionGUIStyle.focused.background = texture2D;
      BehaviorDesignerUtility.selectionGUIStyle.normal.textColor = Color.white;
      BehaviorDesignerUtility.selectionGUIStyle.active.textColor = Color.white;
      BehaviorDesignerUtility.selectionGUIStyle.hover.textColor = Color.white;
      BehaviorDesignerUtility.selectionGUIStyle.focused.textColor = Color.white;
    }

    private static void InitSharedVariableToolbarPopup()
    {
      BehaviorDesignerUtility.sharedVariableToolbarPopup = new GUIStyle(EditorStyles.toolbarPopup);
      BehaviorDesignerUtility.sharedVariableToolbarPopup.margin = new RectOffset(4, 4, 0, 0);
    }

    private static void InitLabelWrapGUIStyle()
    {
      BehaviorDesignerUtility.labelWrapGUIStyle = new GUIStyle(GUI.skin.label);
      BehaviorDesignerUtility.labelWrapGUIStyle.wordWrap = true;
      BehaviorDesignerUtility.labelWrapGUIStyle.alignment = (TextAnchor) 4;
    }

    private static void InitLabelTitleGUIStyle()
    {
      BehaviorDesignerUtility.labelTitleGUIStyle = new GUIStyle(GUI.skin.label);
      BehaviorDesignerUtility.labelTitleGUIStyle.wordWrap = true;
      BehaviorDesignerUtility.labelTitleGUIStyle.alignment = (TextAnchor) 4;
      BehaviorDesignerUtility.labelTitleGUIStyle.fontSize = 14;
    }

    private static void InitBoldLabelGUIStyle()
    {
      BehaviorDesignerUtility.boldLabelGUIStyle = new GUIStyle(GUI.skin.label);
      BehaviorDesignerUtility.boldLabelGUIStyle.fontStyle = (FontStyle) 1;
    }

    private static void InitToolbarButtonLeftAlignGUIStyle()
    {
      BehaviorDesignerUtility.toolbarButtonLeftAlignGUIStyle = new GUIStyle(EditorStyles.toolbarButton);
      BehaviorDesignerUtility.toolbarButtonLeftAlignGUIStyle.alignment = (TextAnchor) 3;
    }

    private static void InitToolbarLabelGUIStyle()
    {
      BehaviorDesignerUtility.toolbarLabelGUIStyle = new GUIStyle(EditorStyles.label);
      BehaviorDesignerUtility.toolbarLabelGUIStyle.normal.textColor = !EditorGUIUtility.isProSkin ? new Color(0.0f, 0.5f, 0.0f) : new Color(0.0f, 0.7f, 0.0f);
    }

    private static void InitTaskInspectorCommentGUIStyle()
    {
      BehaviorDesignerUtility.taskInspectorCommentGUIStyle = new GUIStyle(GUI.skin.textArea);
      BehaviorDesignerUtility.taskInspectorCommentGUIStyle.wordWrap = true;
    }

    private static void InitTaskInspectorGUIStyle()
    {
      BehaviorDesignerUtility.taskInspectorGUIStyle = new GUIStyle(GUI.skin.label);
      BehaviorDesignerUtility.taskInspectorGUIStyle.alignment = (TextAnchor) 3;
      BehaviorDesignerUtility.taskInspectorGUIStyle.fontSize = 11;
      BehaviorDesignerUtility.taskInspectorGUIStyle.fontStyle = (FontStyle) 0;
    }

    private static void InitToolbarButtonSelectionGUIStyle()
    {
      BehaviorDesignerUtility.toolbarButtonSelectionGUIStyle = new GUIStyle(EditorStyles.toolbarButton);
      BehaviorDesignerUtility.toolbarButtonSelectionGUIStyle.normal.background = BehaviorDesignerUtility.toolbarButtonSelectionGUIStyle.active.background;
    }

    private static void InitPreferencesPaneGUIStyle()
    {
      BehaviorDesignerUtility.preferencesPaneGUIStyle = new GUIStyle();
      Texture2D texture2D = new Texture2D(1, 1, TextureFormat.RGBA32, false);
      Color color = !EditorGUIUtility.isProSkin ? new Color(0.706f, 0.706f, 0.706f) : new Color(0.2f, 0.2f, 0.2f, 1f);
      texture2D.SetPixel(1, 1, color);
      texture2D.hideFlags = HideFlags.HideAndDontSave;
      texture2D.Apply();
      BehaviorDesignerUtility.preferencesPaneGUIStyle.normal.background = texture2D;
    }

    private static void InitPropertyBoxGUIStyle()
    {
      BehaviorDesignerUtility.propertyBoxGUIStyle = new GUIStyle();
      BehaviorDesignerUtility.propertyBoxGUIStyle.padding = new RectOffset(2, 2, 0, 0);
      Texture2D texture2D = new Texture2D(1, 1, TextureFormat.RGBA32, false);
      Color color = !EditorGUIUtility.isProSkin ? new Color(0.706f, 0.706f, 0.706f) : new Color(0.2f, 0.2f, 0.2f, 1f);
      texture2D.SetPixel(1, 1, color);
      texture2D.hideFlags = HideFlags.HideAndDontSave;
      texture2D.Apply();
      BehaviorDesignerUtility.propertyBoxGUIStyle.normal.background = texture2D;
    }

    private static void InitPlainButtonGUIStyle()
    {
      BehaviorDesignerUtility.plainButtonGUIStyle = new GUIStyle(GUI.skin.button);
      BehaviorDesignerUtility.plainButtonGUIStyle.border = new RectOffset(0, 0, 0, 0);
      BehaviorDesignerUtility.plainButtonGUIStyle.margin = new RectOffset(0, 0, 2, 2);
      BehaviorDesignerUtility.plainButtonGUIStyle.padding = new RectOffset(0, 0, 1, 0);
      BehaviorDesignerUtility.plainButtonGUIStyle.normal.background = (Texture2D) null;
      BehaviorDesignerUtility.plainButtonGUIStyle.active.background = (Texture2D) null;
      BehaviorDesignerUtility.plainButtonGUIStyle.hover.background = (Texture2D) null;
      BehaviorDesignerUtility.plainButtonGUIStyle.focused.background = (Texture2D) null;
      BehaviorDesignerUtility.plainButtonGUIStyle.normal.textColor = Color.white;
      BehaviorDesignerUtility.plainButtonGUIStyle.active.textColor = Color.white;
      BehaviorDesignerUtility.plainButtonGUIStyle.hover.textColor = Color.white;
      BehaviorDesignerUtility.plainButtonGUIStyle.focused.textColor = Color.white;
    }

    private static void InitTransparentButtonGUIStyle()
    {
      BehaviorDesignerUtility.transparentButtonGUIStyle = new GUIStyle(GUI.skin.button);
      BehaviorDesignerUtility.transparentButtonGUIStyle.border = new RectOffset(0, 0, 0, 0);
      BehaviorDesignerUtility.transparentButtonGUIStyle.margin = new RectOffset(4, 4, 2, 2);
      BehaviorDesignerUtility.transparentButtonGUIStyle.padding = new RectOffset(2, 2, 1, 0);
      BehaviorDesignerUtility.transparentButtonGUIStyle.normal.background = (Texture2D) null;
      BehaviorDesignerUtility.transparentButtonGUIStyle.active.background = (Texture2D) null;
      BehaviorDesignerUtility.transparentButtonGUIStyle.hover.background = (Texture2D) null;
      BehaviorDesignerUtility.transparentButtonGUIStyle.focused.background = (Texture2D) null;
      BehaviorDesignerUtility.transparentButtonGUIStyle.normal.textColor = Color.white;
      BehaviorDesignerUtility.transparentButtonGUIStyle.active.textColor = Color.white;
      BehaviorDesignerUtility.transparentButtonGUIStyle.hover.textColor = Color.white;
      BehaviorDesignerUtility.transparentButtonGUIStyle.focused.textColor = Color.white;
    }

    private static void InitTransparentButtonOffsetGUIStyle()
    {
      BehaviorDesignerUtility.transparentButtonOffsetGUIStyle = new GUIStyle(GUI.skin.button);
      BehaviorDesignerUtility.transparentButtonOffsetGUIStyle.border = new RectOffset(0, 0, 0, 0);
      BehaviorDesignerUtility.transparentButtonOffsetGUIStyle.margin = new RectOffset(4, 4, 4, 2);
      BehaviorDesignerUtility.transparentButtonOffsetGUIStyle.padding = new RectOffset(2, 2, 1, 0);
      BehaviorDesignerUtility.transparentButtonOffsetGUIStyle.normal.background = (Texture2D) null;
      BehaviorDesignerUtility.transparentButtonOffsetGUIStyle.active.background = (Texture2D) null;
      BehaviorDesignerUtility.transparentButtonOffsetGUIStyle.hover.background = (Texture2D) null;
      BehaviorDesignerUtility.transparentButtonOffsetGUIStyle.focused.background = (Texture2D) null;
      BehaviorDesignerUtility.transparentButtonOffsetGUIStyle.normal.textColor = Color.white;
      BehaviorDesignerUtility.transparentButtonOffsetGUIStyle.active.textColor = Color.white;
      BehaviorDesignerUtility.transparentButtonOffsetGUIStyle.hover.textColor = Color.white;
      BehaviorDesignerUtility.transparentButtonOffsetGUIStyle.focused.textColor = Color.white;
    }

    private static void InitButtonGUIStyle()
    {
      BehaviorDesignerUtility.buttonGUIStyle = new GUIStyle(GUI.skin.button);
      BehaviorDesignerUtility.buttonGUIStyle.margin = new RectOffset(0, 0, 2, 2);
      BehaviorDesignerUtility.buttonGUIStyle.padding = new RectOffset(0, 0, 1, 1);
    }

    private static void InitPlainTextureGUIStyle()
    {
      BehaviorDesignerUtility.plainTextureGUIStyle = new GUIStyle();
      BehaviorDesignerUtility.plainTextureGUIStyle.border = new RectOffset(0, 0, 0, 0);
      BehaviorDesignerUtility.plainTextureGUIStyle.margin = new RectOffset(0, 0, 0, 0);
      BehaviorDesignerUtility.plainTextureGUIStyle.padding = new RectOffset(0, 0, 0, 0);
      BehaviorDesignerUtility.plainTextureGUIStyle.normal.background = (Texture2D) null;
      BehaviorDesignerUtility.plainTextureGUIStyle.active.background = (Texture2D) null;
      BehaviorDesignerUtility.plainTextureGUIStyle.hover.background = (Texture2D) null;
      BehaviorDesignerUtility.plainTextureGUIStyle.focused.background = (Texture2D) null;
    }

    private static void InitSelectedBackgroundGUIStyle()
    {
      Texture2D texture2D = new Texture2D(1, 1, TextureFormat.RGBA32, false);
      Color color = !EditorGUIUtility.isProSkin ? new Color(0.243f, 0.5686f, 0.839f, 0.5f) : new Color(0.188f, 0.4588f, 0.6862f, 0.5f);
      texture2D.SetPixel(1, 1, color);
      texture2D.hideFlags = HideFlags.HideAndDontSave;
      texture2D.Apply();
      BehaviorDesignerUtility.selectedBackgroundGUIStyle = new GUIStyle();
      BehaviorDesignerUtility.selectedBackgroundGUIStyle.border = new RectOffset(0, 0, 0, 0);
      BehaviorDesignerUtility.selectedBackgroundGUIStyle.margin = new RectOffset(0, 0, -2, 2);
      BehaviorDesignerUtility.selectedBackgroundGUIStyle.normal.background = texture2D;
      BehaviorDesignerUtility.selectedBackgroundGUIStyle.active.background = texture2D;
      BehaviorDesignerUtility.selectedBackgroundGUIStyle.hover.background = texture2D;
      BehaviorDesignerUtility.selectedBackgroundGUIStyle.focused.background = texture2D;
    }

    private static void InitErrorListDarkBackground()
    {
      Texture2D texture2D = new Texture2D(1, 1, TextureFormat.RGBA32, false);
      Color color = !EditorGUIUtility.isProSkin ? new Color(0.706f, 0.706f, 0.706f) : new Color(0.2f, 0.2f, 0.2f, 1f);
      texture2D.SetPixel(1, 1, color);
      texture2D.hideFlags = HideFlags.HideAndDontSave;
      texture2D.Apply();
      BehaviorDesignerUtility.errorListDarkBackground = new GUIStyle();
      BehaviorDesignerUtility.errorListDarkBackground.padding = new RectOffset(2, 0, 2, 0);
      BehaviorDesignerUtility.errorListDarkBackground.normal.background = texture2D;
      BehaviorDesignerUtility.errorListDarkBackground.active.background = texture2D;
      BehaviorDesignerUtility.errorListDarkBackground.hover.background = texture2D;
      BehaviorDesignerUtility.errorListDarkBackground.focused.background = texture2D;
      BehaviorDesignerUtility.errorListDarkBackground.normal.textColor = !EditorGUIUtility.isProSkin ? new Color(0.206f, 0.206f, 0.206f) : new Color(0.706f, 0.706f, 0.706f);
      BehaviorDesignerUtility.errorListDarkBackground.alignment = (TextAnchor) 0;
      BehaviorDesignerUtility.errorListDarkBackground.wordWrap = true;
    }

    private static void InitErrorListLightBackground()
    {
      BehaviorDesignerUtility.errorListLightBackground = new GUIStyle();
      BehaviorDesignerUtility.errorListLightBackground.padding = new RectOffset(2, 0, 2, 0);
      BehaviorDesignerUtility.errorListLightBackground.normal.textColor = !EditorGUIUtility.isProSkin ? new Color(0.106f, 0.106f, 0.106f) : new Color(0.706f, 0.706f, 0.706f);
      BehaviorDesignerUtility.errorListLightBackground.alignment = (TextAnchor) 0;
      BehaviorDesignerUtility.errorListLightBackground.wordWrap = true;
    }

    private static void InitWelcomeScreenIntroGUIStyle()
    {
      BehaviorDesignerUtility.welcomeScreenIntroGUIStyle = new GUIStyle(GUI.skin.label);
      BehaviorDesignerUtility.welcomeScreenIntroGUIStyle.fontSize = 16;
      BehaviorDesignerUtility.welcomeScreenIntroGUIStyle.fontStyle = (FontStyle) 1;
      BehaviorDesignerUtility.welcomeScreenIntroGUIStyle.normal.textColor = new Color(0.706f, 0.706f, 0.706f);
    }

    private static void InitWelcomeScreenTextHeaderGUIStyle()
    {
      BehaviorDesignerUtility.welcomeScreenTextHeaderGUIStyle = new GUIStyle(GUI.skin.label);
      BehaviorDesignerUtility.welcomeScreenTextHeaderGUIStyle.alignment = (TextAnchor) 3;
      BehaviorDesignerUtility.welcomeScreenTextHeaderGUIStyle.fontSize = 14;
      BehaviorDesignerUtility.welcomeScreenTextHeaderGUIStyle.fontStyle = (FontStyle) 1;
    }

    private static void InitWelcomeScreenTextDescriptionGUIStyle()
    {
      BehaviorDesignerUtility.welcomeScreenTextDescriptionGUIStyle = new GUIStyle(GUI.skin.label);
      BehaviorDesignerUtility.welcomeScreenTextDescriptionGUIStyle.wordWrap = true;
    }

    private static void InitTaskBorderTexture(int colorIndex) => BehaviorDesignerUtility.taskBorderTexture[colorIndex] = BehaviorDesignerUtility.LoadTaskTexture("TaskBorder" + BehaviorDesignerUtility.ColorIndexToColorString(colorIndex) + ".png");

    private static void InitTaskBorderRunningTexture() => BehaviorDesignerUtility.taskBorderRunningTexture = BehaviorDesignerUtility.LoadTaskTexture("TaskBorderRunning.png");

    private static void InitTaskBorderIdentifyTexture() => BehaviorDesignerUtility.taskBorderIdentifyTexture = BehaviorDesignerUtility.LoadTaskTexture("TaskBorderIdentify.png");

    private static void InitTaskConnectionTopTexture(int colorIndex) => BehaviorDesignerUtility.taskConnectionTopTexture[colorIndex] = BehaviorDesignerUtility.LoadTaskTexture("TaskConnectionTop" + BehaviorDesignerUtility.ColorIndexToColorString(colorIndex) + ".png");

    private static void InitTaskConnectionBottomTexture(int colorIndex) => BehaviorDesignerUtility.taskConnectionBottomTexture[colorIndex] = BehaviorDesignerUtility.LoadTaskTexture("TaskConnectionBottom" + BehaviorDesignerUtility.ColorIndexToColorString(colorIndex) + ".png");

    private static void InitTaskConnectionRunningTopTexture() => BehaviorDesignerUtility.taskConnectionRunningTopTexture = BehaviorDesignerUtility.LoadTaskTexture("TaskConnectionRunningTop.png");

    private static void InitTaskConnectionRunningBottomTexture() => BehaviorDesignerUtility.taskConnectionRunningBottomTexture = BehaviorDesignerUtility.LoadTaskTexture("TaskConnectionRunningBottom.png");

    private static void InitTaskConnectionIdentifyTopTexture() => BehaviorDesignerUtility.taskConnectionIdentifyTopTexture = BehaviorDesignerUtility.LoadTaskTexture("TaskConnectionIdentifyTop.png");

    private static void InitTaskConnectionIdentifyBottomTexture() => BehaviorDesignerUtility.taskConnectionIdentifyBottomTexture = BehaviorDesignerUtility.LoadTaskTexture("TaskConnectionIdentifyBottom.png");

    private static void InitTaskConnectionCollapsedTexture() => BehaviorDesignerUtility.taskConnectionCollapsedTexture = BehaviorDesignerUtility.LoadTaskTexture("TaskConnectionCollapsed.png");

    private static void InitContentSeparatorTexture() => BehaviorDesignerUtility.contentSeparatorTexture = BehaviorDesignerUtility.LoadTexture("ContentSeparator.png");

    private static void InitDocTexture() => BehaviorDesignerUtility.docTexture = BehaviorDesignerUtility.LoadTexture("DocIcon.png");

    private static void InitGearTexture() => BehaviorDesignerUtility.gearTexture = BehaviorDesignerUtility.LoadTexture("GearIcon.png");

    private static void InitColorSelectorTexture(int colorIndex) => BehaviorDesignerUtility.colorSelectorTexture[colorIndex] = BehaviorDesignerUtility.LoadTexture("ColorSelector" + BehaviorDesignerUtility.ColorIndexToColorString(colorIndex) + ".png");

    private static void InitVariableButtonTexture() => BehaviorDesignerUtility.variableButtonTexture = BehaviorDesignerUtility.LoadTexture("VariableButton.png");

    private static void InitVariableButtonSelectedTexture() => BehaviorDesignerUtility.variableButtonSelectedTexture = BehaviorDesignerUtility.LoadTexture("VariableButtonSelected.png");

    private static void InitVariableWatchButtonTexture() => BehaviorDesignerUtility.variableWatchButtonTexture = BehaviorDesignerUtility.LoadTexture("VariableWatchButton.png");

    private static void InitVariableWatchButtonSelectedTexture() => BehaviorDesignerUtility.variableWatchButtonSelectedTexture = BehaviorDesignerUtility.LoadTexture("VariableWatchButtonSelected.png");

    private static void InitReferencedTexture() => BehaviorDesignerUtility.referencedTexture = BehaviorDesignerUtility.LoadTexture("LinkedIcon.png");

    private static void InitConditionalAbortSelfTexture() => BehaviorDesignerUtility.conditionalAbortSelfTexture = BehaviorDesignerUtility.LoadTexture("ConditionalAbortSelfIcon.png");

    private static void InitConditionalAbortLowerPriorityTexture() => BehaviorDesignerUtility.conditionalAbortLowerPriorityTexture = BehaviorDesignerUtility.LoadTexture("ConditionalAbortLowerPriorityIcon.png");

    private static void InitConditionalAbortBothTexture() => BehaviorDesignerUtility.conditionalAbortBothTexture = BehaviorDesignerUtility.LoadTexture("ConditionalAbortBothIcon.png");

    private static void InitDeleteButtonTexture() => BehaviorDesignerUtility.deleteButtonTexture = BehaviorDesignerUtility.LoadTexture("DeleteButton.png");

    private static void InitVariableDeleteButtonTexture() => BehaviorDesignerUtility.variableDeleteButtonTexture = BehaviorDesignerUtility.LoadTexture("VariableDeleteButton.png");

    private static void InitDownArrowButtonTexture() => BehaviorDesignerUtility.downArrowButtonTexture = BehaviorDesignerUtility.LoadTexture("DownArrowButton.png");

    private static void InitUpArrowButtonTexture() => BehaviorDesignerUtility.upArrowButtonTexture = BehaviorDesignerUtility.LoadTexture("UpArrowButton.png");

    private static void InitVariableMapButtonTexture() => BehaviorDesignerUtility.variableMapButtonTexture = BehaviorDesignerUtility.LoadTexture("VariableMapButton.png");

    private static void InitIdentifyButtonTexture() => BehaviorDesignerUtility.identifyButtonTexture = BehaviorDesignerUtility.LoadTexture("IdentifyButton.png");

    private static void InitBreakpointTexture() => BehaviorDesignerUtility.breakpointTexture = BehaviorDesignerUtility.LoadTexture("BreakpointIcon.png", false);

    private static void InitErrorIconTexture() => BehaviorDesignerUtility.errorIconTexture = BehaviorDesignerUtility.LoadTexture("ErrorIcon.png");

    private static void InitSmallErrorIconTexture() => BehaviorDesignerUtility.smallErrorIconTexture = BehaviorDesignerUtility.LoadTexture("SmallErrorIcon.png");

    private static void InitEnableTaskTexture() => BehaviorDesignerUtility.enableTaskTexture = BehaviorDesignerUtility.LoadTexture("TaskEnableIcon.png", false);

    private static void InitDisableTaskTexture() => BehaviorDesignerUtility.disableTaskTexture = BehaviorDesignerUtility.LoadTexture("TaskDisableIcon.png", false);

    private static void InitExpandTaskTexture() => BehaviorDesignerUtility.expandTaskTexture = BehaviorDesignerUtility.LoadTexture("TaskExpandIcon.png", false);

    private static void InitCollapseTaskTexture() => BehaviorDesignerUtility.collapseTaskTexture = BehaviorDesignerUtility.LoadTexture("TaskCollapseIcon.png", false);

    private static void InitExecutionSuccessTexture() => BehaviorDesignerUtility.executionSuccessTexture = BehaviorDesignerUtility.LoadTexture("ExecutionSuccess.png", false);

    private static void InitExecutionFailureTexture() => BehaviorDesignerUtility.executionFailureTexture = BehaviorDesignerUtility.LoadTexture("ExecutionFailure.png", false);

    private static void InitExecutionSuccessRepeatTexture() => BehaviorDesignerUtility.executionSuccessRepeatTexture = BehaviorDesignerUtility.LoadTexture("ExecutionSuccessRepeat.png", false);

    private static void InitExecutionFailureRepeatTexture() => BehaviorDesignerUtility.executionFailureRepeatTexture = BehaviorDesignerUtility.LoadTexture("ExecutionFailureRepeat.png", false);

    private static void InitHistoryBackwardTexture() => BehaviorDesignerUtility.historyBackwardTexture = BehaviorDesignerUtility.LoadTexture("HistoryBackward.png");

    private static void InitHistoryForwardTexture() => BehaviorDesignerUtility.historyForwardTexture = BehaviorDesignerUtility.LoadTexture("HistoryForward.png");

    private static void InitPlayTexture() => BehaviorDesignerUtility.playTexture = BehaviorDesignerUtility.LoadTexture("Play.png");

    private static void InitPauseTexture() => BehaviorDesignerUtility.pauseTexture = BehaviorDesignerUtility.LoadTexture("Pause.png");

    private static void InitStepTexture() => BehaviorDesignerUtility.stepTexture = BehaviorDesignerUtility.LoadTexture("Step.png");

    private static void InitScreenshotBackgroundTexture()
    {
      BehaviorDesignerUtility.screenshotBackgroundTexture = new Texture2D(1, 1, TextureFormat.RGB24, false);
      if (EditorGUIUtility.isProSkin)
        BehaviorDesignerUtility.screenshotBackgroundTexture.SetPixel(1, 1, new Color(0.1647f, 0.1647f, 0.1647f));
      else
        BehaviorDesignerUtility.screenshotBackgroundTexture.SetPixel(1, 1, new Color(0.3647f, 0.3647f, 0.3647f));
      BehaviorDesignerUtility.screenshotBackgroundTexture.Apply();
    }

    public static void SetObjectDirty(UnityEngine.Object obj)
    {
      EditorUtility.SetDirty(obj);
      PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
      if (EditorApplication.isPlaying || EditorUtility.IsPersistent(obj))
        return;
      if (obj is Component)
        EditorSceneManager.MarkSceneDirty((obj as Component).gameObject.scene);
      else if (obj is GameObject)
      {
        EditorSceneManager.MarkSceneDirty((obj as GameObject).scene);
      }
      else
      {
        if (EditorUtility.IsPersistent(obj))
          return;
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
      }
    }
  }
}
