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
        "Behavior Designer/Runtime/Core.Unity",
        "Behavior Designer/Runtime/Tasks.Unity",
    };

    // 指定临时存放的路径
    private static string tempFolder = "TempExcluded/Behavior Designer/";

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
        string defines =
            PlayerSettings.GetScriptingDefineSymbolsForGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
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

    private static void MoveFolderOrFiles(string originPrefix, string destPath)
    {
        foreach (var path in excludePaths)
        {
            try
            {
                var originPath = Path.Combine(originPrefix, path);
                if (File.Exists(originPath))
                {
                    Debug.LogError($"将文件{originPath}移动到{destPath}");
                    File.Move(originPath, destPath);
                }
                else if (Directory.Exists(originPath))
                {
                    var directoryName = Path.GetFileName(originPath);
                    var destPath2 = Path.Combine(destPath, directoryName);
                    if (Directory.Exists(destPath2))
                    {
                        Directory.Delete(destPath2, true);
                    }
                    Debug.LogError($"将文件夹{originPath}移动到{destPath2}");
                    Directory.Move(originPath, destPath2);
                }
            }
            catch (IOException e)
            {
                Debug.LogError(e);
            }
        }
    }
}