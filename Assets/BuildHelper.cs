using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
public static class BuildHelper
{
    
    // 指定要排除的文件夹路径
    private static string[] excludePaths =
    {
        "Behavior Designer/Editor", 
        "Behavior Designer/Editor.meta", 
        "Behavior Designer/Runtime/Core.Unity", 
        "Behavior Designer/Runtime/Core.Unity.meta", 
        "Behavior Designer/Runtime/Tasks.Unity",
        "Behavior Designer/Runtime/Tasks.Unity.meta"
    };
    // 指定临时存放的路径
    private static string tempFolder = "TempExcluded";
    
#if UNITY_PLATFORM
    [MenuItem("BuildTools/ChangeDefine/Remove UNITY_PLATFORM")]
    public static void RemoveUnityPlatform()
    {
        EnableDefineSymbols("UNITY_PLATFORM", false);
    }
#else
    [MenuItem("BuildTools/ChangeDefine/Add UNITY_PLATFORM")]
    public static void AddUnityPlatform()
    {
        EnableDefineSymbols("UNITY_PLATFORM", true);
    }
#endif

    public static void EnableDefineSymbols(string symbols, bool enable)
    {
        Debug.Log($"EnableDefineSymbols {symbols} {enable}");
        string defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
        var ss = defines.Split(';').ToList();
        if (enable)
        {
            if (ss.Contains(symbols))
            {
                return;
            }

            ss.Add(symbols);
        }
        else
        {
            if (!ss.Contains(symbols))
            {
                return;
            }

            ss.Remove(symbols);
        }

        BuildHelper.ShowNotification($"EnableDefineSymbols {symbols} {enable}");
        defines = string.Join(";", ss);
        PlayerSettings.SetScriptingDefineSymbolsForGroup(EditorUserBuildSettings.selectedBuildTargetGroup, defines);
        BuildHelper.HandleExcludeFiles(enable);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    public static void ShowNotification(string tips)
    {
        EditorWindow game = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
        game?.ShowNotification(new GUIContent($"{tips}"));
    }

    public static void HandleExcludeFiles(bool enable)
    {
        var tempPath = Path.GetFullPath(Path.Combine("./", tempFolder));
        if (!Directory.Exists(tempFolder))
        {
            Directory.CreateDirectory(tempFolder);
        }

        var originPrefix = enable ? tempPath : Application.dataPath;
        var destPrefix = enable ? Application.dataPath : tempPath;
        BuildHelper.MoveFolderOrFiles(originPrefix, destPrefix);
    }
    private static void MoveFolderOrFiles(string originPrefix, string destPrefix)
    {
        foreach (var path in excludePaths)
        {
            try
            {
                var fullPath = Path.Combine(originPrefix, path);
                if (File.Exists(fullPath))
                {
                    Debug.LogError($"将文件{fullPath}移动到{Path.Combine(destPrefix, path)}");
                    File.Move(fullPath, Path.Combine(destPrefix, path));
                }
                else if (Directory.Exists(fullPath))
                {
                    Debug.LogError($"将文件夹{fullPath}移动到{Path.Combine(destPrefix, path)}");
                    Directory.Move(fullPath, Path.Combine(destPrefix, path));
                }
            }
            catch (IOException e)
            {
                Debug.LogError(e);
            }
        }
    }
}